using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ironmanx_04_2.Ai;
using Microsoft.Win32;

namespace ironmanx_04_2
{
	public partial class ReportSearchWindow : Window
	{
		private CancellationTokenSource _cts;
		private AiSettings _settings;
		private ReportIndex _index;

		public sealed class ResultItem
		{
			public string Header { get; set; }
			public string Path { get; set; }
			public string Explanation { get; set; }
			public string Summary { get; set; }
			public string CopiedTo { get; set; }
		}

		public ReportSearchWindow()
		{
			InitializeComponent();
			_settings = AiSettings.Load();
			FolderText.Text = _settings.SearchLastFolder;
			OutputText.Text = _settings.EffectiveSearchOutputFolder;
			AiCheck.IsChecked = _settings.IsOpenAiConfigured;
			ReloadIndex();
		}

		private void ReloadIndex()
		{
			try { _index = ReportIndex.Load(_settings.EffectiveReportIndexPath); }
			catch (Exception ex)
			{
				Log("Could not read index (" + ex.Message + "), using the default index.");
				_index = ReportIndex.Load(null);
			}
			UpdateIndexInfo();
		}

		private void UpdateIndexInfo()
		{
			var all = _index.All();
			IndexInfoText.Text = "Master index: " + _index.FilePath + "  -  " + all.Count + " report(s), " +
								 all.Count(e => e.HasContent) + " content-scanned, " + all.Count(e => !string.IsNullOrWhiteSpace(e.Summary)) + " summarized.";
		}

		private void BrowseFolder_Click(object sender, RoutedEventArgs e)
		{
			var f = PickFolder("Select the folder to search", FolderText.Text);
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

		private bool ValidateFolder()
		{
			var folder = (FolderText.Text ?? "").Trim();
			if (folder.Length == 0 || !Directory.Exists(Environment.ExpandEnvironmentVariables(folder))) { Log("Choose an existing folder to search."); return false; }
			_settings = AiSettings.Load();
			_settings.SearchLastFolder = folder;
			try { _settings.Save(); } catch { }
			return true;
		}

		private async void Search_Click(object sender, RoutedEventArgs e)
		{
			if (!ValidateFolder()) return;
			if (string.IsNullOrWhiteSpace(QueryText.Text)) { Log("Describe the report you are looking for."); return; }
			var opts = new ReportSearchOptions
			{
				Query = QueryText.Text.Trim(),
				RootFolder = FolderText.Text.Trim(),
				Recursive = RecursiveCheck.IsChecked == true,
				OutputFolder = (OutputText.Text ?? "").Trim(),
				DeepScanCount = _settings.SearchDeepScanCount,
				MaxResults = _settings.SearchMaxResults,
				UseAi = AiCheck.IsChecked == true,
				CopyResults = CopyCheck.IsChecked == true
			};
			ResultList.ItemsSource = null;
			await RunAsync("Searching...", async (searcher, ct) =>
			{
				var r = await Task.Run(() => searcher.SearchAsync(opts, ct), ct);
				ResultList.ItemsSource = r.Item1.Select((x, i) => new ResultItem
				{
					Header = (i + 1) + ". " + Path.GetFileName(x.Path) + (x.AiScore >= 0 ? "   (AI relevance " + x.AiScore + "/100)" : "   (score " + x.TotalScore.ToString("0.#") + ")"),
					Path = x.Path,
					Explanation = "Why: " + x.Explanation,
					Summary = x.Summary,
					CopiedTo = x.CopiedTo
				}).ToList();
				Log(r.Item1.Count + " relevant report(s).");
				if (r.Item2 != null)
				{
					Log("Output: " + r.Item2);
					try { Process.Start("explorer.exe", "\"" + r.Item2 + "\""); } catch { }
				}
			});
		}

		private async void Learn_Click(object sender, RoutedEventArgs e)
		{
			if (!ValidateFolder()) return;
			var folder = FolderText.Text.Trim();
			var recursive = RecursiveCheck.IsChecked == true;
			var ai = LearnAiCheck.IsChecked == true;
			if (MessageBox.Show(this, "Learning mode will open and index every report under:\n" + folder +
					(ai ? "\n\nAI summaries are enabled - this may take a long time and use API tokens." : "") + "\n\nContinue?",
					"Learning mode", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
			await RunAsync("Learning...", (searcher, ct) => Task.Run(() => searcher.BuildIndexAsync(folder, recursive, ai, ct), ct));
		}

		private async Task RunAsync(string status, Func<ReportSearcher, CancellationToken, Task> work)
		{
			if (!string.Equals(_index.FilePath, _settings.EffectiveReportIndexPath, StringComparison.OrdinalIgnoreCase)) ReloadIndex();
			var searcher = new ReportSearcher(_settings, _index);
			searcher.Progress += m => Dispatcher.BeginInvoke(new Action(() => Log(m)));
			_cts = new CancellationTokenSource();
			SetBusy(true, status);
			try
			{
				await work(searcher, _cts.Token);
				SetBusy(false, "Done.");
			}
			catch (OperationCanceledException) { Log("Stopped by user."); SetBusy(false, "Stopped"); try { _index.Save(); } catch { } }
			catch (Exception ex) { Log("ERROR: " + ex.Message); SetBusy(false, "Failed"); }
			finally { _cts.Dispose(); _cts = null; UpdateIndexInfo(); }
		}

		private void ResultList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
		{
			var item = ResultList.SelectedItem as ResultItem;
			if (item == null) return;
			try { Process.Start("explorer.exe", "/select,\"" + item.Path + "\""); } catch (Exception ex) { Log("ERROR: " + ex.Message); }
		}

		private void OpenIndex_Click(object sender, RoutedEventArgs e)
		{
			try
			{
				if (File.Exists(_index.FilePath)) Process.Start("explorer.exe", "/select,\"" + _index.FilePath + "\"");
				else Log("Index has not been created yet: " + _index.FilePath);
			}
			catch (Exception ex) { Log("ERROR: " + ex.Message); }
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
			SearchButton.IsEnabled = !busy;
			LearnButton.IsEnabled = !busy;
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
