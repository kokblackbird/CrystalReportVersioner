using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ironmanx_04_2.Ai
{
    public sealed class ReportSearchOptions
    {
        public string Query;
        public string RootFolder;
        public bool Recursive = true;
        public string OutputFolder;
        /// <summary>How many top path/index candidates get escalated to a formula/content scan.</summary>
        public int DeepScanCount = 15;
        /// <summary>How many of the best content matches get an AI summary and explanation.</summary>
        public int MaxResults = 5;
        public bool UseAi = true;
        public bool CopyResults = true;
    }

    public sealed class ReportSearchResult
    {
        public string Path;
        public double PathScore;
        public double IndexScore;
        public double ContentScore;
        public int AiScore = -1;
        public string Summary = string.Empty;
        public string Explanation = string.Empty;
        public List<string> MatchedTerms = new List<string>();
        public string CopiedTo;
        public double TotalScore { get { return AiScore >= 0 ? AiScore + PathScore + IndexScore + ContentScore : PathScore + IndexScore + ContentScore; } }
    }

    /// <summary>
    /// Searches a directory for the reports most relevant to a natural-language query.
    /// Stage 1 scores file paths (plus anything already in the master index), stage 2 escalates the best candidates
    /// to a formula/structure scan, stage 3 has the AI summarize and explain the best matches. Results are copied to an
    /// output folder together with a search summary and the original paths. Everything scanned is added to the index.
    /// </summary>
    public sealed class ReportSearcher
    {
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","for","with","that","this","from","report","reports","rpt","show","shows","find","which","what","where",
            "are","was","were","all","any","each","per","into","about","need","want","looking","look","one","has","have","not",
            "true","false","null","name","text","type","value","kind","formula","formulas","main","field","fields","section"
        };

        private readonly AiSettings _settings;
        private readonly ReportIndex _index;
        public event Action<string> Progress;

        public ReportSearcher(AiSettings settings, ReportIndex index)
        {
            _settings = settings;
            _index = index;
        }

        public static List<string> Tokenize(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            // Split camelCase / PascalCase as well as punctuation.
            var spaced = Regex.Replace(text, "([a-z])([A-Z])", "$1 $2");
            foreach (Match m in Regex.Matches(spaced, "[A-Za-z0-9]+"))
            {
                var t = m.Value.ToLowerInvariant();
                if (t.Length < 2 || StopWords.Contains(t)) continue;
                if (t.All(char.IsDigit) && t.Length < 4) continue;
                result.Add(t);
            }
            return result;
        }

        // ── Learning mode ──────────────────────────────────────────────────────────────────
        /// <summary>Scans every report under the folder into the index (skips reports that are unchanged since last scan).</summary>
        public async Task<int> BuildIndexAsync(string folder, bool recursive, bool aiSummaries, CancellationToken ct)
        {
            var files = Enumerate(folder, recursive);
            Report("Learning mode: " + files.Count + " report(s) found under " + folder);
            var client = aiSummaries && _settings.IsOpenAiConfigured ? new OpenAiClient(_settings) : null;
            if (aiSummaries && client == null) Report("OpenAI is not configured - indexing structure only.");
            int n = 0, scanned = 0;
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                var e = _index.GetOrCreate(f);
                var fi = new FileInfo(f);
                bool needScan = !e.IsFreshFor(fi) || !e.HasContent;
                bool needSummary = client != null && string.IsNullOrWhiteSpace(e.Summary);
                if (!needScan && !needSummary) continue;
                Report("[" + n + "/" + files.Count + "] " + f);
                string specJson = await ScanContentAsync(e, fi, needScan).ConfigureAwait(false);
                if (needSummary && specJson != null) await SummarizeIntoAsync(client, e, specJson, ct).ConfigureAwait(false);
                scanned++;
                if (scanned % 10 == 0) SaveIndex();
            }
            SaveIndex();
            Report("Learning mode finished: " + scanned + " report(s) (re)indexed, " + _index.Count + " in index.");
            return scanned;
        }

        // ── Search ─────────────────────────────────────────────────────────────────────────
        public async Task<Tuple<List<ReportSearchResult>, string>> SearchAsync(ReportSearchOptions o, CancellationToken ct)
        {
            var queryTokens = Tokenize(o.Query).Distinct().ToList();
            if (queryTokens.Count == 0) throw new InvalidOperationException("Enter some search words describing the report.");
            var files = Enumerate(o.RootFolder, o.Recursive);
            Report("Stage 1: scoring " + files.Count + " file path(s) and the master index (" + _index.Count + " entries)...");

            var all = new List<ReportSearchResult>();
            foreach (var f in files)
            {
                var r = new ReportSearchResult { Path = f, PathScore = ScorePath(f, queryTokens) };
                var e = _index.Get(f);
                if (e != null) r.IndexScore = ScoreIndex(e, queryTokens, r.MatchedTerms);
                all.Add(r);
            }
            var candidates = all.Where(r => r.PathScore + r.IndexScore > 0)
                .OrderByDescending(r => r.PathScore + r.IndexScore).ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, o.DeepScanCount)).ToList();
            Report("   " + all.Count(r => r.PathScore > 0) + " path match(es), " + all.Count(r => r.IndexScore > 0) + " index match(es).");

            // Fill remaining deep-scan slots with reports not yet indexed so every search teaches the index something.
            if (candidates.Count < o.DeepScanCount)
            {
                var extra = all.Except(candidates).Where(r => { var e = _index.Get(r.Path); return e == null || !e.HasContent; })
                    .Take(o.DeepScanCount - candidates.Count).ToList();
                if (extra.Count > 0) Report("   Adding " + extra.Count + " unindexed report(s) to the content scan.");
                candidates.AddRange(extra);
            }

            Report("Stage 2: scanning formulas/structure of " + candidates.Count + " candidate(s)...");
            var specCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            foreach (var c in candidates)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                var e = _index.GetOrCreate(c.Path);
                var fi = new FileInfo(c.Path);
                if (!e.IsFreshFor(fi) || !e.HasContent)
                {
                    Report("   [" + i + "/" + candidates.Count + "] scanning " + Path.GetFileName(c.Path));
                    var json = await ScanContentAsync(e, fi, true).ConfigureAwait(false);
                    if (json != null) specCache[c.Path] = json;
                }
                c.MatchedTerms.Clear();
                c.IndexScore = ScoreIndex(e, queryTokens, c.MatchedTerms);
                c.ContentScore = ScoreContent(e, queryTokens, c.MatchedTerms);
            }
            SaveIndex();

            var ranked = candidates.Where(c => c.PathScore + c.IndexScore + c.ContentScore > 0)
                .OrderByDescending(c => c.PathScore + c.IndexScore + c.ContentScore).ToList();
            var top = ranked.Take(Math.Max(1, o.MaxResults)).ToList();
            if (top.Count == 0) Report("No relevant reports found.");

            var client = o.UseAi && _settings.IsOpenAiConfigured ? new OpenAiClient(_settings) : null;
            if (o.UseAi && client == null) Report("OpenAI is not configured - using keyword explanations only.");
            if (top.Count > 0) Report("Stage 3: summarizing " + top.Count + " best match(es)...");
            foreach (var c in top)
            {
                ct.ThrowIfCancellationRequested();
                var e = _index.GetOrCreate(c.Path);
                if (client != null && string.IsNullOrWhiteSpace(e.Summary))
                {
                    string json;
                    if (!specCache.TryGetValue(c.Path, out json)) json = await ScanContentAsync(e, new FileInfo(c.Path), false).ConfigureAwait(false);
                    if (json != null)
                    {
                        Report("   AI summarizing " + Path.GetFileName(c.Path));
                        await SummarizeIntoAsync(client, e, json, ct).ConfigureAwait(false);
                    }
                }
                c.Summary = string.IsNullOrWhiteSpace(e.Summary) ? HeuristicSummary(e) : e.Summary;
                c.Explanation = HeuristicExplanation(c);
            }
            if (client != null && top.Count > 0)
            {
                try { await AiRankAsync(client, o.Query, top, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { Report("   AI ranking failed: " + ex.Message); }
                top = top.OrderByDescending(c => c.TotalScore).ToList();
            }

            // Learn: remember the query terms against reports judged relevant.
            foreach (var c in top.Where(c => c.AiScore < 0 || c.AiScore >= 40)) _index.Learn(c.Path, queryTokens);
            SaveIndex();

            string outDir = null;
            if (o.CopyResults && top.Count > 0 && !string.IsNullOrWhiteSpace(o.OutputFolder))
                outDir = WriteOutput(o, top, files.Count, candidates.Count);
            return Tuple.Create(top, outDir);
        }

        // ── Scoring ────────────────────────────────────────────────────────────────────────
        private static double ScorePath(string path, List<string> q)
        {
            var nameTokens = new HashSet<string>(Tokenize(Path.GetFileNameWithoutExtension(path)));
            var dirTokens = new HashSet<string>(Tokenize(Path.GetDirectoryName(path)));
            var nameLower = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            double s = 0;
            foreach (var t in q)
            {
                if (nameTokens.Contains(t)) s += 3;
                else if (t.Length >= 3 && (nameLower.Contains(t) || nameTokens.Any(n => n.Length >= 3 && (n.StartsWith(t) || t.StartsWith(n))))) s += 1.5;
                else if (dirTokens.Contains(t)) s += 1;
            }
            return s;
        }

        private static double ScoreIndex(ReportIndexEntry e, List<string> q, List<string> matched)
        {
            double s = 0;
            var learned = new HashSet<string>((e.LearnedTerms ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            var summary = new HashSet<string>(Tokenize(e.Summary));
            foreach (var t in q)
            {
                if (learned.Contains(t)) { s += 3; AddOnce(matched, t + " (learned)"); }
                if (summary.Contains(t)) { s += 2; AddOnce(matched, t + " (summary)"); }
            }
            if (s > 0 && e.Hits > 0) s += Math.Min(3, Math.Log(1 + e.Hits));
            return s;
        }

        private static double ScoreContent(ReportIndexEntry e, List<string> q, List<string> matched)
        {
            if (!e.HasContent) return 0;
            var kw = new HashSet<string>(e.Keywords.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            double s = 0;
            foreach (var t in q)
            {
                if (kw.Contains(t)) { s += 2; AddOnce(matched, t); }
                else if (t.Length >= 4 && kw.Any(k => k.Length >= 4 && (k.StartsWith(t) || t.StartsWith(k)))) { s += 1; AddOnce(matched, t + "~"); }
            }
            return s;
        }

        private static void AddOnce(List<string> list, string v) { if (!list.Contains(v)) list.Add(v); }

        // ── Content scan / AI ──────────────────────────────────────────────────────────────
        /// <summary>Opens the report, refreshes index keywords if needed and returns the spec JSON (null on failure).</summary>
        private async Task<string> ScanContentAsync(ReportIndexEntry e, FileInfo fi, bool updateKeywords)
        {
            try
            {
                Tuple<Dictionary<string, object>, string, string> spec;
                using (var tools = await ReportAgentTools.OpenAsync(fi.FullName, _settings).ConfigureAwait(false))
                    spec = await tools.GetSpecAsync().ConfigureAwait(false);
                if (updateKeywords)
                {
                    var text = ReportAgentTools.FormulasMarkdown(spec.Item1) + "\n" + spec.Item3;
                    var tokens = Tokenize(text).Where(t => !t.All(char.IsDigit)).Distinct().Take(4000);
                    bool changed = !e.IsFreshFor(fi);
                    e.Keywords = string.Join(" ", tokens);
                    e.FileSize = fi.Length;
                    e.LastWriteUtc = fi.LastWriteTimeUtc;
                    e.IndexedUtc = DateTime.UtcNow;
                    e.Error = string.Empty;
                    if (changed) e.Summary = string.Empty;
                }
                return spec.Item3;
            }
            catch (Exception ex)
            {
                e.Error = ex.Message;
                e.FileSize = fi.Exists ? fi.Length : 0;
                e.LastWriteUtc = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
                e.IndexedUtc = DateTime.UtcNow;
                Report("   could not open " + fi.Name + ": " + ex.Message);
                return null;
            }
        }

        private async Task SummarizeIntoAsync(OpenAiClient client, ReportIndexEntry e, string specJson, CancellationToken ct)
        {
            if (specJson.Length > 60000) specJson = specJson.Substring(0, 60000) + "...(truncated)";
            var system =
@"You are an expert SAP Crystal Reports analyst. Given the JSON structure of a report, write ONE plain-English paragraph
(4-7 sentences, no headings or bullets) describing what the report actually does: its business purpose, main data sources,
filters/parameters, grouping/totals and what the key formulas calculate. Do not invent anything not in the JSON.";
            var messages = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "role", "system" }, { "content", system } },
                new Dictionary<string, object> { { "role", "user" }, { "content", "File: " + Path.GetFileName(e.Path) + "\n\n" + specJson } }
            };
            try
            {
                var r = await client.ChatAsync(messages, null, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(r.Content)) e.Summary = r.Content.Trim().Replace("\r", " ").Replace("\n", " ");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Report("   AI summary failed: " + ex.Message); }
        }

        private async Task AiRankAsync(OpenAiClient client, string query, List<ReportSearchResult> top, CancellationToken ct)
        {
            Report("   AI judging relevance...");
            var sb = new StringBuilder();
            sb.AppendLine("User is looking for: " + query);
            sb.AppendLine();
            for (int i = 0; i < top.Count; i++)
            {
                sb.AppendLine("#" + (i + 1) + " " + top[i].Path);
                sb.AppendLine("Matched terms: " + string.Join(", ", top[i].MatchedTerms));
                sb.AppendLine("Summary: " + top[i].Summary);
                sb.AppendLine();
            }
            var system =
@"You rank Crystal Reports by relevance to what the user is looking for. For EVERY candidate output exactly one line:
<number>|<relevance 0-100>|<one or two sentence explanation of why it does or does not match>
Output nothing else.";
            var messages = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "role", "system" }, { "content", system } },
                new Dictionary<string, object> { { "role", "user" }, { "content", sb.ToString() } }
            };
            var r = await client.ChatAsync(messages, null, ct).ConfigureAwait(false);
            foreach (var line in (r.Content ?? "").Replace("\r", "").Split('\n'))
            {
                var parts = line.Trim().TrimStart('#').Split(new[] { '|' }, 3);
                int n, score;
                if (parts.Length < 3 || !int.TryParse(parts[0].Trim(), out n) || !int.TryParse(parts[1].Trim().TrimEnd('%'), out score)) continue;
                if (n < 1 || n > top.Count) continue;
                top[n - 1].AiScore = Math.Max(0, Math.Min(100, score));
                top[n - 1].Explanation = parts[2].Trim();
            }
        }

        private static string HeuristicSummary(ReportIndexEntry e)
        {
            if (!string.IsNullOrEmpty(e.Error) && !e.HasContent) return "Could not be opened: " + e.Error;
            var kw = (e.Keywords ?? "").Split(' ').Where(k => k.Length > 3).Take(25);
            return "No AI summary available. Notable terms in the report structure: " + string.Join(", ", kw) + ".";
        }

        private static string HeuristicExplanation(ReportSearchResult c)
        {
            var parts = new List<string>();
            if (c.PathScore > 0) parts.Add("file path matches the query (score " + c.PathScore.ToString("0.#", CultureInfo.InvariantCulture) + ")");
            if (c.MatchedTerms.Count > 0) parts.Add("report content/index matches: " + string.Join(", ", c.MatchedTerms));
            return parts.Count == 0 ? "Weak match." : char.ToUpperInvariant(parts[0][0]) + string.Join("; ", parts).Substring(1) + ".";
        }

        // ── Output ─────────────────────────────────────────────────────────────────────────
        private string WriteOutput(ReportSearchOptions o, List<ReportSearchResult> top, int fileCount, int scannedCount)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var slug = string.Join("-", Tokenize(o.Query).Take(5));
            var root = Path.Combine(o.OutputFolder, "search_" + stamp + (slug.Length > 0 ? "_" + slug : ""));
            Directory.CreateDirectory(root);
            var md = new StringBuilder();
            md.AppendLine("# Report search: " + o.Query);
            md.AppendLine();
            md.AppendLine("- Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            md.AppendLine("- Searched folder: " + o.RootFolder + (o.Recursive ? " (including subfolders)" : ""));
            md.AppendLine("- Reports considered: " + fileCount + ", content-scanned: " + scannedCount);
            md.AppendLine("- Index: " + _index.FilePath);
            md.AppendLine();
            md.AppendLine("Ranking: stage 1 scored file paths and the master index, stage 2 scanned formulas/tables of the best candidates, stage 3 " +
                          (top.Any(t => t.AiScore >= 0) ? "had the AI summarize and judge relevance." : "used keyword matching (AI not used)."));
            md.AppendLine();
            var csv = new StringBuilder("Rank,FileName,OriginalPath,CopiedTo,Score\r\n");
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int rank = 0;
            foreach (var c in top)
            {
                rank++;
                var baseName = Path.GetFileNameWithoutExtension(c.Path);
                var name = rank.ToString("00") + "_" + baseName;
                for (int k = 2; !used.Add(name); k++) name = rank.ToString("00") + "_" + baseName + "_" + k;
                var dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                try
                {
                    var dest = Path.Combine(dir, Path.GetFileName(c.Path));
                    File.Copy(c.Path, dest, true);
                    c.CopiedTo = dest;
                }
                catch (Exception ex) { Report("   copy failed for " + c.Path + ": " + ex.Message); }
                var info = new StringBuilder();
                info.AppendLine("# " + Path.GetFileName(c.Path));
                info.AppendLine();
                info.AppendLine("**Original path:** " + c.Path);
                info.AppendLine();
                info.AppendLine("**Why it matched:** " + c.Explanation);
                info.AppendLine();
                info.AppendLine("**What it does:** " + c.Summary);
                File.WriteAllText(Path.Combine(dir, "ABOUT.md"), info.ToString(), Encoding.UTF8);
                File.WriteAllText(Path.Combine(dir, "ORIGINAL_PATH.txt"), c.Path, Encoding.UTF8);

                md.AppendLine("## " + rank + ". " + Path.GetFileName(c.Path));
                md.AppendLine();
                md.AppendLine("- Original path: `" + c.Path + "`");
                md.AppendLine("- Copied to: `" + name + "/`");
                md.AppendLine("- Scores: path " + c.PathScore.ToString("0.#", CultureInfo.InvariantCulture) + ", index " + c.IndexScore.ToString("0.#", CultureInfo.InvariantCulture) +
                              ", content " + c.ContentScore.ToString("0.#", CultureInfo.InvariantCulture) + (c.AiScore >= 0 ? ", AI relevance " + c.AiScore + "/100" : ""));
                md.AppendLine("- Why: " + c.Explanation);
                md.AppendLine();
                md.AppendLine(c.Summary);
                md.AppendLine();
                csv.AppendLine(rank + "," + Q(Path.GetFileName(c.Path)) + "," + Q(c.Path) + "," + Q(c.CopiedTo) + "," + c.TotalScore.ToString("0.#", CultureInfo.InvariantCulture));
            }
            File.WriteAllText(Path.Combine(root, "SEARCH_SUMMARY.md"), md.ToString(), Encoding.UTF8);
            File.WriteAllText(Path.Combine(root, "original-paths.csv"), csv.ToString(), Encoding.UTF8);
            Report("Copied " + top.Count + " report(s) -> " + root);
            return root;
        }

        private static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }

        private static List<string> Enumerate(string folder, bool recursive)
        {
            var path = Environment.ExpandEnvironmentVariables((folder ?? "").Trim().Trim('"'));
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException("Folder not found: " + folder);
            var list = new List<string>();
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                try { list.AddRange(Directory.GetFiles(d, "*.rpt")); } catch { }
                if (!recursive) continue;
                try { foreach (var s in Directory.GetDirectories(d)) stack.Push(s); } catch { }
            }
            return list.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void SaveIndex()
        {
            try { _index.Save(); } catch (Exception ex) { Report("   could not save index: " + ex.Message); }
        }

        private void Report(string msg)
        {
            var h = Progress;
            if (h != null) h(msg);
        }
    }
}
