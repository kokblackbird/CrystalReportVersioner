using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ironmanx_04_2.Ai;

namespace ironmanx_04_2
{
    public partial class AiSettingsWindow : Window
    {
        private readonly AiSettings _settings;

        public AiSettingsWindow()
        {
            InitializeComponent();
            _settings = AiSettings.Load();
            LoadIntoUi();
        }

        private void LoadIntoUi()
        {
            ApiKeyBox.Password = _settings.OpenAiApiKey;
            BaseUrlText.Text = _settings.OpenAiBaseUrl;
            ModelCombo.Text = _settings.OpenAiModel;
            SelectCombo(EffortCombo, _settings.ReasoningEffort);
            OrgText.Text = _settings.OpenAiOrganization;
            ProjectText.Text = _settings.OpenAiProject;
            TemperatureText.Text = _settings.Temperature.ToString(CultureInfo.InvariantCulture);
            MaxIterationsText.Text = _settings.MaxIterations.ToString(CultureInfo.InvariantCulture);
            TimeoutText.Text = _settings.RequestTimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            ExtraInstructionsText.Text = _settings.ExtraInstructions;

            LoadDsns();
            ConnStringText.Text = _settings.DbConnectionString;
            SelectCombo(ProviderCombo, _settings.DbProvider);
            SelectAccessMode(_settings.DataAccessMode);
            SensitiveText.Text = (_settings.SensitiveKeywords ?? string.Empty).Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            UpdateProviderUi();
            MaxRowsText.Text = _settings.SqlQueryMaxRows.ToString(CultureInfo.InvariantCulture);
            SqlTimeoutText.Text = _settings.SqlCommandTimeoutSeconds.ToString(CultureInfo.InvariantCulture);

            ValidateRenderCheck.IsChecked = _settings.ValidateByRendering;
            SendPagesCheck.IsChecked = _settings.SendRenderedPagesToAi;
            CrServerText.Text = _settings.CrystalServer;
            CrDatabaseText.Text = _settings.CrystalDatabase;
            CrIntegratedCheck.IsChecked = _settings.CrystalIntegratedSecurity;
            CrUserText.Text = _settings.CrystalUser;
            CrPasswordBox.Password = _settings.CrystalPassword;
        }

        private bool ReadFromUi(out string error)
        {
            error = null;
            double temp;
            int maxIter, timeout, maxRows, sqlTimeout;
            if (!double.TryParse(TemperatureText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out temp) || temp < 0 || temp > 2) { error = "Temperature must be a number between 0 and 2."; return false; }
            if (!int.TryParse(MaxIterationsText.Text, out maxIter) || maxIter < 1 || maxIter > 200) { error = "Max agent steps must be 1-200."; return false; }
            if (!int.TryParse(TimeoutText.Text, out timeout) || timeout < 10) { error = "Request timeout must be at least 10 seconds."; return false; }
            if (!int.TryParse(MaxRowsText.Text, out maxRows) || maxRows < 1 || maxRows > 1000) { error = "Max rows must be 1-1000."; return false; }
            if (!int.TryParse(SqlTimeoutText.Text, out sqlTimeout) || sqlTimeout < 1) { error = "SQL timeout must be at least 1 second."; return false; }

            _settings.OpenAiApiKey = ApiKeyBox.Password.Trim();
            _settings.OpenAiBaseUrl = string.IsNullOrWhiteSpace(BaseUrlText.Text) ? "https://api.openai.com/v1" : BaseUrlText.Text.Trim().TrimEnd('/');
            _settings.OpenAiModel = (ModelCombo.Text ?? string.Empty).Trim();
            var effort = EffortCombo.SelectedItem as ComboBoxItem;
            _settings.ReasoningEffort = effort != null ? effort.Content.ToString() : "default";
            _settings.OpenAiOrganization = OrgText.Text.Trim();
            _settings.OpenAiProject = ProjectText.Text.Trim();
            _settings.Temperature = temp;
            _settings.MaxIterations = maxIter;
            _settings.RequestTimeoutSeconds = timeout;
            _settings.ExtraInstructions = ExtraInstructionsText.Text;

            var provider = ProviderCombo.SelectedItem as ComboBoxItem;
            _settings.DbProvider = provider != null ? provider.Content.ToString() : "SqlClient";
            _settings.DbConnectionString = ConnStringText.Text.Trim();
            var mode = AccessModeCombo.SelectedItem as ComboBoxItem;
            _settings.DataAccessMode = mode != null ? (string)mode.Tag : DataAccessModes.SchemaOnly;
            _settings.SensitiveKeywords = SensitiveText.Text ?? string.Empty;
            _settings.SqlQueryMaxRows = maxRows;
            _settings.SqlCommandTimeoutSeconds = sqlTimeout;

            _settings.ValidateByRendering = ValidateRenderCheck.IsChecked == true;
            _settings.SendRenderedPagesToAi = SendPagesCheck.IsChecked == true;
            _settings.CrystalServer = CrServerText.Text.Trim();
            _settings.CrystalDatabase = CrDatabaseText.Text.Trim();
            _settings.CrystalIntegratedSecurity = CrIntegratedCheck.IsChecked == true;
            _settings.CrystalUser = CrUserText.Text.Trim();
            _settings.CrystalPassword = CrPasswordBox.Password;
            return true;
        }

        private void SelectAccessMode(string value)
        {
            foreach (ComboBoxItem cbi in AccessModeCombo.Items)
            {
                if (string.Equals((string)cbi.Tag, value, StringComparison.OrdinalIgnoreCase)) { AccessModeCombo.SelectedItem = cbi; return; }
            }
            AccessModeCombo.SelectedIndex = 0;
        }

        private bool IsOdbcSelected
        {
            get
            {
                var p = ProviderCombo.SelectedItem as ComboBoxItem;
                return p != null && string.Equals(p.Content as string, "Odbc", StringComparison.OrdinalIgnoreCase);
            }
        }

        private void LoadDsns()
        {
            var current = DsnCombo.Text;
            DsnCombo.Items.Clear();
            foreach (var n in DatabaseSchemaTools.GetOdbcDsnNames()) DsnCombo.Items.Add(n);
            if (string.IsNullOrEmpty(current))
            {
                var m = System.Text.RegularExpressions.Regex.Match(ConnStringText.Text ?? _settings.DbConnectionString ?? string.Empty, @"(?i)\bDSN\s*=\s*([^;]+)");
                if (m.Success) current = m.Groups[1].Value.Trim();
            }
            DsnCombo.Text = current ?? string.Empty;
        }

        private void UpdateProviderUi()
        {
            var odbc = IsOdbcSelected;
            DsnLabel.Visibility = odbc ? Visibility.Visible : Visibility.Collapsed;
            DsnPanel.Visibility = odbc ? Visibility.Visible : Visibility.Collapsed;
            var p = ProviderCombo.SelectedItem as ComboBoxItem;
            var name = p != null ? p.Content as string : "SqlClient";
            switch (name)
            {
                case "Odbc": ConnHintText.Text = "Pick a DSN above (fills DSN=...;) and add UID=...;PWD=...; if needed. Or use Driver={...};Server=...;Database=...;  (stored encrypted)"; break;
                case "OleDb": ConnHintText.Text = "e.g. Provider=MSOLEDBSQL;Data Source=SERVER;Initial Catalog=Sales;Integrated Security=SSPI;  (stored encrypted)"; break;
                default: ConnHintText.Text = "e.g. Server=MYSQL01;Database=Sales;Integrated Security=True  (stored encrypted)"; break;
            }
        }

        private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DsnPanel != null) UpdateProviderUi();
        }

        private void RefreshDsn_Click(object sender, RoutedEventArgs e)
        {
            LoadDsns();
            StatusText.Text = DsnCombo.Items.Count + " ODBC data source(s) found (" + (Environment.Is64BitProcess ? "64" : "32") + "-bit).";
        }

        private void DsnCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var dsn = DsnCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(dsn)) return;
            var cs = ConnStringText.Text ?? string.Empty;
            var re = new System.Text.RegularExpressions.Regex(@"(?i)\bDSN\s*=\s*[^;]*");
            ConnStringText.Text = re.IsMatch(cs) ? re.Replace(cs, "DSN=" + dsn, 1) : ("DSN=" + dsn + ";" + (cs.Length > 0 && !cs.StartsWith(";") ? cs : cs.TrimStart(';')));
        }

        private static void SelectCombo(ComboBox combo, string value)
        {
            foreach (var item in combo.Items)
            {
                var cbi = item as ComboBoxItem;
                if (cbi != null && string.Equals(cbi.Content as string, value, StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedItem = cbi;
                    return;
                }
            }
            combo.SelectedIndex = 0;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string error;
            if (!ReadFromUi(out error)) { MessageBox.Show(this, error, "Invalid setting", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            try
            {
                _settings.Save();
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not save settings: " + ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LoadModels_Click(object sender, RoutedEventArgs e)
        {
            string error;
            if (!ReadFromUi(out error)) { StatusText.Text = error; return; }
            StatusText.Text = "Loading models from your OpenAI account...";
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var models = await new OpenAiClient(_settings).ListModelsAsync(cts.Token);
                    var current = ModelCombo.Text;
                    ModelCombo.Items.Clear();
                    foreach (var m in models) ModelCombo.Items.Add(new ComboBoxItem { Content = m });
                    ModelCombo.Text = current;
                    ModelCombo.IsDropDownOpen = true;
                    StatusText.Text = models.Count + " chat models available to this key.";
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not load models: " + ex.Message;
            }
        }

        private async void TestOpenAi_Click(object sender, RoutedEventArgs e)
        {
            string error;
            if (!ReadFromUi(out error)) { StatusText.Text = error; return; }
            StatusText.Text = "Contacting OpenAI...";
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_settings.RequestTimeoutSeconds)))
                {
                    var client = new OpenAiClient(_settings);
                    var reply = await client.PingAsync(cts.Token);
                    StatusText.Text = "OpenAI OK: " + reply;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "OpenAI failed: " + ex.Message;
            }
        }

        private async void TestDb_Click(object sender, RoutedEventArgs e)
        {
            string error;
            if (!ReadFromUi(out error)) { StatusText.Text = error; return; }
            StatusText.Text = "Connecting to database...";
            var tools = new DatabaseSchemaTools(_settings);
            var result = await Task.Run(() => tools.TestConnection());
            StatusText.Text = result;
        }
    }
}
