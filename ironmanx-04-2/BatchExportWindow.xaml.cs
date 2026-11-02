using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using ironmanx_04_2.Ai;
using Microsoft.Win32;

namespace ironmanx_04_2
{
    public partial class BatchExportWindow : Window
    {
        private CancellationTokenSource _cts;
        private readonly ObservableCollection<ReportItem> _items = new ObservableCollection<ReportItem>();

        public sealed class ReportItem : INotifyPropertyChanged
        {
            private bool _isSelected;
            public string Path { get; set; }
            public bool IsSelected
            {
                get { return _isSelected; }
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    var h = PropertyChanged;
                    if (h != null) h(this, new PropertyChangedEventArgs("IsSelected"));
                }
            }
            public event PropertyChangedEventHandler PropertyChanged;
        }

        public BatchExportWindow()
        {
            InitializeComponent();
            ReportList.ItemsSource = _items;
            OutputText.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReportExports");
        }

        private void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var f = PickFolder("Select a folder containing .rpt files", FolderText.Text);
            if (f != null) FolderText.Text = f;
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var f = PickFolder("Select the output folder", OutputText.Text);
            if (f != null) OutputText.Text = f;
        }

        private string PickFolder(string title, string start)
        {
            // Folder picker via SaveFileDialog trick (no WinForms dependency).
            var dlg = new SaveFileDialog { Title = title, FileName = "Select this folder", Filter = "Folder|*." };
            if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start)) dlg.InitialDirectory = start;
            return dlg.ShowDialog(this) == true ? Path.GetDirectoryName(dlg.FileName) : null;
        }

        private void AddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog { Title = "Select reports", Filter = "Crystal Reports (*.rpt)|*.rpt", Multiselect = true };
            if (dlg.ShowDialog(this) != true) return;
            var existing = (PathsText.Text ?? "").Trim().TrimEnd(',');
            var added = string.Join(", ", dlg.FileNames);
            PathsText.Text = existing.Length == 0 ? added : existing + ", " + added;
        }

        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            LoadList();
        }

        private bool LoadList()
        {
            try
            {
                var list = BatchReportExporter.ResolvePaths(FolderText.Text, RecursiveCheck.IsChecked == true, PathsText.Text);
                var previous = _items.ToDictionary(i => i.Path, i => i.IsSelected, StringComparer.OrdinalIgnoreCase);
                _items.Clear();
                foreach (var p in list)
                {
                    bool sel;
                    var item = new ReportItem { Path = p, IsSelected = !previous.TryGetValue(p, out sel) || sel };
                    item.PropertyChanged += (s, a) => UpdateCount();
                    _items.Add(item);
                }
                UpdateCount();
                Log(list.Count + " report(s) found.");
                return true;
            }
            catch (Exception ex) { Log("ERROR: " + ex.Message); return false; }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var i in _items) i.IsSelected = true;
        }

        private void SelectNone_Click(object sender, RoutedEventArgs e)
        {
            foreach (var i in _items) i.IsSelected = false;
        }

        private void UpdateCount()
        {
            ListCountText.Text = "Reports: " + _items.Count(i => i.IsSelected) + " of " + _items.Count + " selected";
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            var settings = AiSettings.Load();
            var opts = new BatchExportOptions
            {
                OutputFolder = (OutputText.Text ?? "").Trim(),
                IncludeFullSpec = SpecCheck.IsChecked == true,
                AiSummary = AiCheck.IsChecked == true,
                Zip = ZipCheck.IsChecked == true,
                ExtraInstructions = InstructionsText.Text
            };
            try
            {
                if (_items.Count == 0 && !LoadList()) return;
                opts.ReportPaths = _items.Where(i => i.IsSelected).Select(i => i.Path).ToList();
                if (opts.ReportPaths.Count == 0) { Log("No reports selected. Enter a folder and/or comma-delimited paths, click Preview list and tick reports."); return; }
                if (string.IsNullOrWhiteSpace(opts.OutputFolder)) { Log("Choose an output folder."); return; }
                Directory.CreateDirectory(opts.OutputFolder);
            }
            catch (Exception ex) { Log("ERROR: " + ex.Message); return; }

            var exporter = new BatchReportExporter(settings);
            exporter.Progress += m => Dispatcher.BeginInvoke(new Action(() => Log(m)));
            _cts = new CancellationTokenSource();
            SetBusy(true, "Exporting " + opts.ReportPaths.Count + " report(s)...");
            try
            {
                var result = await exporter.RunAsync(opts, _cts.Token);
                SetBusy(false, "Done.");
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + result + "\""); } catch { }
            }
            catch (OperationCanceledException) { Log("Stopped by user."); SetBusy(false, "Stopped"); }
            catch (Exception ex) { Log("ERROR: " + ex.Message); SetBusy(false, "Failed"); }
            finally { _cts.Dispose(); _cts = null; }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) _cts.Cancel();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null) _cts.Cancel();
            Close();
        }

        private void SetBusy(bool busy, string status)
        {
            RunButton.IsEnabled = !busy;
            StopButton.IsEnabled = busy;
            StatusText.Text = status;
        }

        private void Log(string text)
        {
            LogText.AppendText(text + Environment.NewLine);
            LogText.ScrollToEnd();
        }
    }
}
