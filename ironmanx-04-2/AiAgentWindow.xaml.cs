using System;
using System.IO;
using System.Linq;
using System.Windows.Controls;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ironmanx_04_2.Ai;
using Microsoft.Win32;

namespace ironmanx_04_2
{
    public partial class AiAgentWindow : Window
    {
        private readonly string _reportPath;
        private AiSettings _settings;
        private ReportAgentTools _tools;
        private ReportAgent _agent;
        private CancellationTokenSource _cts;
        private bool _busy;

        /// <summary>Path of the saved, AI-modified report (null if not saved).</summary>
        public string SavedPath { get; private set; }

        public AiAgentWindow(string reportPath)
        {
            InitializeComponent();
            _reportPath = reportPath;
            ReportPathText.Text = "Report: " + reportPath;
            Loaded += async (s, e) => await InitializeSessionAsync();
            Closing += AiAgentWindow_Closing;
        }

        private async Task InitializeSessionAsync()
        {
            _settings = AiSettings.Load();
            SetBusy(true, "Loading report through the Crystal runtime...");
            try
            {
                _tools = await ReportAgentTools.OpenAsync(_reportPath, _settings);
                _tools.PdfRendered += path => Dispatcher.BeginInvoke(new Action(() =>
                {
                    Append("Preview PDF rendered: " + path);
                    try { System.Diagnostics.Process.Start(path); } catch { }
                }));
                RefreshResources();
                Append("Report loaded. Describe the change you want (formulas, layout, sections, groups, tables, subreports...).");
                if (!_settings.IsOpenAiConfigured) Append("OpenAI is not configured yet — click 'AI Settings...' to add your API key.");
                if (!_settings.IsDatabaseConfigured) Append("No live database connection configured — the agent will only know the tables already in the report.");
                SetBusy(false, "Ready");
            }
            catch (Exception ex)
            {
                SetBusy(false, "Failed to load report");
                Append("ERROR loading report: " + ex.Message);
                SendButton.IsEnabled = false;
            }
        }

        private void EnsureAgent()
        {
            if (_agent != null) return;
            _agent = new ReportAgent(_settings, _tools);
            _agent.Progress += msg => Dispatcher.BeginInvoke(new Action(() => Append(msg)));
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _tools == null) return;
            var prompt = (PromptText.Text ?? string.Empty).Trim();
            if (prompt.Length == 0) return;
            if (!_settings.IsOpenAiConfigured)
            {
                MessageBox.Show(this, "Configure your OpenAI API key in AI Settings first.", "AI Agent", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            EnsureAgent();
            PromptText.Clear();
            Append("");
            Append("YOU: " + prompt);
            _cts = new CancellationTokenSource();
            SetBusy(true, "Agent working...");
            try
            {
                var answer = await _agent.RunAsync(prompt, _cts.Token);
                Append("");
                Append("AI: " + answer);
                SetBusy(false, string.Format("Done. Tokens used: {0} prompt / {1} completion.", _agent.TotalPromptTokens, _agent.TotalCompletionTokens));
            }
            catch (OperationCanceledException)
            {
                Append("Stopped by user.");
                SetBusy(false, "Stopped");
            }
            catch (Exception ex)
            {
                Append("ERROR: " + ex.Message);
                SetBusy(false, "Error");
            }
            finally
            {
                _cts.Dispose();
                _cts = null;
                RefreshChanges();
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) _cts.Cancel();
        }

        private void PromptText_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                Send_Click(sender, e);
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AiSettingsWindow { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                _settings = AiSettings.Load();
                // Rebuild the agent so new model/key/instructions apply; report edits are kept.
                _agent = null;
                if (_tools != null)
                    Append("Settings updated. The next message starts a new conversation (report changes are kept).");
            }
        }

        private async void Save_Click(object sender, RoutedEventArgs e)
        {
            if (_tools == null) return;
            var dir = Path.GetDirectoryName(_reportPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(_reportPath) + "_ai_" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".rpt";
            var dlg = new SaveFileDialog
            {
                Title = "Save AI-modified report",
                Filter = "Crystal Report (*.rpt)|*.rpt",
                FileName = name,
                InitialDirectory = dir
            };
            if (!dlg.ShowDialog(this).GetValueOrDefault()) return;

            SetBusy(true, "Saving...");
            try
            {
                await _tools.SaveAsAsync(dlg.FileName);
                SavedPath = dlg.FileName;
                Append("Saved: " + dlg.FileName);
                SetBusy(false, "Saved. Close this window to use it as the Changed report.");
            }
            catch (Exception ex)
            {
                Append("ERROR saving: " + ex.Message);
                SetBusy(false, "Save failed");
            }
        }

        private void AiAgentWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_busy)
            {
                MessageBox.Show(this, "The agent is still working. Click Stop first.", "AI Agent", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Cancel = true;
                return;
            }
            if (_tools != null && _tools.IsDirty && SavedPath == null)
            {
                var r = MessageBox.Show(this, "The report has unsaved AI changes. Discard them?", "AI Agent", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            }
            var tools = _tools;
            _tools = null;
            if (tools != null) Task.Run(() => tools.Dispose());
        }

        private void RefreshResources()
        {
            ResourcesList.Items.Clear();
            if (_tools == null) return;
            foreach (var d in _tools.Resources.Documents)
                ResourcesList.Items.Add(new ListBoxItem
                {
                    Content = d.Name + (d.Error != null ? "  (error)" : "  (" + (d.Text.Length / 1000.0).ToString("0.#") + "k chars)"),
                    ToolTip = d.Path + (d.Error != null ? Environment.NewLine + d.Error : string.Empty),
                    Tag = d.Path
                });
        }

        private async void ApplyResources(System.Collections.Generic.List<string> paths)
        {
            _settings.ResourceFiles = paths;
            try { _settings.Save(); } catch (Exception ex) { Append("Could not save resource list: " + ex.Message); }
            if (_tools != null)
            {
                SetBusy(true, "Reading reference documents...");
                var tools = _tools;
                try { await Task.Run(() => tools.SetResources(paths)); }
                finally { SetBusy(false, "Ready"); }
            }
            _agent = null; // rebuild prompt + tool list with the new documents
            RefreshResources();
            Append("Reference documents updated (" + paths.Count + "). The next message starts a new conversation; report changes are kept.");
        }

        private void AddResourcePaths(System.Collections.Generic.IEnumerable<string> files)
        {
            var paths = (_settings.ResourceFiles ?? new System.Collections.Generic.List<string>()).ToList();
            var skipped = new System.Collections.Generic.List<string>();
            foreach (var f in files)
            {
                if (!ResourceLibrary.IsSupported(f)) { skipped.Add(Path.GetFileName(f)); continue; }
                if (!paths.Contains(f, StringComparer.OrdinalIgnoreCase)) paths.Add(f);
            }
            if (skipped.Count > 0) Append("Skipped unsupported files (use .txt/.md/.docx/.sql/...): " + string.Join(", ", skipped));
            ApplyResources(paths);
        }

        private void AddResource_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            var dlg = new OpenFileDialog { Title = "Add reference documents", Filter = ResourceLibrary.FileDialogFilter, Multiselect = true };
            if (dlg.ShowDialog(this) == true) AddResourcePaths(dlg.FileNames);
        }

        private void RemoveResource_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || ResourcesList.SelectedItems.Count == 0) return;
            var remove = ResourcesList.SelectedItems.Cast<ListBoxItem>().Select(i => (string)i.Tag).ToList();
            ApplyResources((_settings.ResourceFiles ?? new System.Collections.Generic.List<string>())
                .Where(p => !remove.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList());
        }

        private void ResourcesList_Drop(object sender, DragEventArgs e)
        {
            if (_busy || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            AddResourcePaths((string[])e.Data.GetData(DataFormats.FileDrop));
        }

        private void RefreshChanges()
        {
            ChangesList.Items.Clear();
            if (_tools == null) return;
            foreach (var c in _tools.ChangeLog) ChangesList.Items.Add(c);
            SaveButton.IsEnabled = !_busy && _tools.IsDirty;
        }

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            SendButton.IsEnabled = !busy && _tools != null;
            StopButton.IsEnabled = busy && _cts != null;
            SaveButton.IsEnabled = !busy && _tools != null && _tools.IsDirty;
            StatusText.Text = status;
        }

        private void Append(string line)
        {
            TranscriptText.AppendText(line + Environment.NewLine);
            TranscriptText.ScrollToEnd();
            RefreshChangesLight();
        }

        private void RefreshChangesLight()
        {
            if (_tools == null) return;
            if (ChangesList.Items.Count != _tools.ChangeLog.Count)
            {
                ChangesList.Items.Clear();
                foreach (var c in _tools.ChangeLog.ToArray()) ChangesList.Items.Add(c);
            }
        }
    }
}
