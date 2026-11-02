using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ironmanx_04_2.Ai
{
    public sealed class BatchExportOptions
    {
        public List<string> ReportPaths = new List<string>();
        public string OutputFolder;
        public bool IncludeFullSpec = true;
        public bool AiSummary = true;
        public bool Zip = true;
        public string ExtraInstructions;
    }

    /// <summary>
    /// Exports formulas (and optionally the full conversion spec and an AI-written summary) for many reports,
    /// then optionally zips the output so it can be sent easily.
    /// </summary>
    public sealed class BatchReportExporter
    {
        private readonly AiSettings _settings;
        public event Action<string> Progress;

        public BatchReportExporter(AiSettings settings) { _settings = settings; }

        /// <summary>Resolves folders (recursive *.rpt), files, and comma/semicolon/newline separated lists.</summary>
        public static List<string> ResolvePaths(string folder, bool recursive, string delimitedPaths)
        {
            var result = new List<string>();
            var inputs = new List<string>();
            if (!string.IsNullOrWhiteSpace(folder)) inputs.Add(folder);
            if (!string.IsNullOrWhiteSpace(delimitedPaths))
                inputs.AddRange(delimitedPaths.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim().Trim('"').Trim()).Where(p => p.Length > 0));
            foreach (var p in inputs)
            {
                var path = Environment.ExpandEnvironmentVariables(p);
                if (Directory.Exists(path))
                    result.AddRange(Directory.GetFiles(path, "*.rpt", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly));
                else if (File.Exists(path) && path.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase))
                    result.Add(Path.GetFullPath(path));
                else
                    throw new FileNotFoundException("Not found or not a .rpt file/folder: " + p);
            }
            return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<string> RunAsync(BatchExportOptions o, CancellationToken ct)
        {
            if (o.ReportPaths.Count == 0) throw new InvalidOperationException("No reports selected.");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var root = Path.Combine(o.OutputFolder, "report-export_" + stamp);
            Directory.CreateDirectory(root);
            var useAi = o.AiSummary && _settings.IsOpenAiConfigured;
            if (o.AiSummary && !useAi) Report("OpenAI is not configured - AI summaries skipped.");
            var client = useAi ? new OpenAiClient(_settings) : null;

            var index = new StringBuilder();
            index.AppendLine("# Report export " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            index.AppendLine();
            index.AppendLine("| Report | Formulas | Subreports | Status | Summary |");
            index.AppendLine("|---|---|---|---|---|");
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int ok = 0, failed = 0, n = 0;

            foreach (var path in o.ReportPaths)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                var baseName = Path.GetFileNameWithoutExtension(path);
                var name = baseName;
                for (int i = 2; !usedNames.Add(name); i++) name = baseName + "_" + i;
                var dir = Path.Combine(root, name);
                Report("[" + n + "/" + o.ReportPaths.Count + "] " + path);
                try
                {
                    Directory.CreateDirectory(dir);
                    File.Copy(path, Path.Combine(dir, Path.GetFileName(path)), true);
                    Tuple<Dictionary<string, object>, string, string> spec;
                    using (var tools = await ReportAgentTools.OpenAsync(path, _settings).ConfigureAwait(false))
                        spec = await tools.GetSpecAsync().ConfigureAwait(false);

                    var model = spec.Item1;
                    var formulaCount = CountFormulas(model);
                    var subCount = ((List<object>)model["subreports"]).Count;
                    File.WriteAllText(Path.Combine(dir, "formulas.md"), ReportAgentTools.FormulasMarkdown(model), Encoding.UTF8);
                    if (o.IncludeFullSpec)
                    {
                        File.WriteAllText(Path.Combine(dir, "REPORT_SPEC.md"), spec.Item2, Encoding.UTF8);
                        File.WriteAllText(Path.Combine(dir, "report-spec.json"), spec.Item3, Encoding.UTF8);
                    }

                    string firstLine = "";
                    if (useAi)
                    {
                        Report("   AI summarizing...");
                        try
                        {
                            var summary = await SummarizeAsync(client, spec.Item3, o.ExtraInstructions, ct).ConfigureAwait(false);
                            File.WriteAllText(Path.Combine(dir, "summary.md"), "# Summary: " + baseName + "\n\n" + summary, Encoding.UTF8);
                            firstLine = FirstSentence(summary);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            firstLine = "AI summary failed: " + ex.Message;
                            Report("   " + firstLine);
                        }
                    }
                    index.AppendLine("| [" + baseName + "](" + Uri.EscapeDataString(name) + "/formulas.md) | " + formulaCount + " | " + subCount + " | OK | " + Cell(firstLine) + " |");
                    ok++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    Report("   FAILED: " + ex.Message);
                    try { File.WriteAllText(Path.Combine(dir, "error.txt"), path + Environment.NewLine + ex, Encoding.UTF8); } catch { }
                    index.AppendLine("| " + baseName + " | - | - | FAILED | " + Cell(ex.Message) + " |");
                }
            }

            index.AppendLine();
            index.AppendLine("Exported " + ok + " report(s), " + failed + " failed.");
            index.AppendLine();
            index.AppendLine("Source paths:");
            foreach (var p in o.ReportPaths) index.AppendLine("- " + p);
            File.WriteAllText(Path.Combine(root, "INDEX.md"), index.ToString(), Encoding.UTF8);
            Report("Exported " + ok + " report(s), " + failed + " failed -> " + root);

            if (o.Zip)
            {
                var zip = root + ".zip";
                if (File.Exists(zip)) File.Delete(zip);
                ZipFile.CreateFromDirectory(root, zip, CompressionLevel.Optimal, true);
                Report("Zipped: " + zip);
                return zip;
            }
            return root;
        }

        private async Task<string> SummarizeAsync(OpenAiClient client, string specJson, string extra, CancellationToken ct)
        {
            if (specJson.Length > 120000) specJson = specJson.Substring(0, 120000) + "...(truncated)";
            var system =
@"You are an expert SAP Crystal Reports analyst. Given the JSON structure of a report (tables, joins, parameters,
record/group selection, formulas, grouping, sorting, summaries, subreports and layout), write a concise Markdown summary:
1. **Purpose** - what the report shows and who would use it (1-3 sentences).
2. **Data sources & joins** - tables and how they relate.
3. **Parameters & filtering** - parameters and selection logic in plain English.
4. **Grouping, sorting & totals**.
5. **Key formulas** - what the important formulas compute, in plain English.
6. **Subreports** - what each does and how it is linked.
7. **Notes / risks** - complexity, possible issues, anything unusual.
Do not invent anything not present in the JSON. No row data is provided.";
            if (!string.IsNullOrWhiteSpace(extra)) system += "\n\nAdditional instructions from the user:\n" + extra.Trim();
            var messages = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "role", "system" }, { "content", system } },
                new Dictionary<string, object> { { "role", "user" }, { "content", specJson } }
            };
            var r = await client.ChatAsync(messages, null, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(r.Content) ? "(empty summary)" : r.Content.Trim();
        }

        private static int CountFormulas(Dictionary<string, object> spec)
        {
            int c = ((List<object>)((Dictionary<string, object>)spec["main"])["formulas"]).Count;
            foreach (Dictionary<string, object> s in (List<object>)spec["subreports"]) c += ((List<object>)s["formulas"]).Count;
            return c;
        }

        private static string FirstSentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var lines = s.Replace("\r", "").Split('\n').Select(l => l.Trim().TrimStart('#', '*', '-', ' ', '1', '.')).Where(l => l.Length > 20);
            var first = lines.FirstOrDefault() ?? "";
            return first.Length > 160 ? first.Substring(0, 160) + "..." : first;
        }

        private static string Cell(string s)
        {
            return (s ?? "").Replace("|", "/").Replace("\r", " ").Replace("\n", " ");
        }

        private void Report(string msg)
        {
            var h = Progress;
            if (h != null) h(msg);
        }
    }
}
