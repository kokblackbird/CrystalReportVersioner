using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ironmanx_04_2.Ai
{
    /// <summary>One row of the master report index.</summary>
    public sealed class ReportIndexEntry
    {
        public string Path;
        public long FileSize;
        public DateTime LastWriteUtc;
        public DateTime IndexedUtc;
        /// <summary>Space separated lowercase tokens from tables, fields, parameters, formulas and selection.</summary>
        public string Keywords = string.Empty;
        /// <summary>AI (or heuristic) one-paragraph summary of what the report does.</summary>
        public string Summary = string.Empty;
        /// <summary>Space separated query terms that previously led to this report being chosen as relevant.</summary>
        public string LearnedTerms = string.Empty;
        public int Hits;
        public string Error = string.Empty;

        public bool HasContent { get { return !string.IsNullOrEmpty(Keywords); } }

        public bool IsFreshFor(FileInfo fi)
        {
            return fi != null && fi.Exists && fi.Length == FileSize
                && Math.Abs((fi.LastWriteTimeUtc - LastWriteUtc).TotalSeconds) < 2;
        }
    }

    /// <summary>
    /// Master index of scanned reports, persisted as a CSV file (default %APPDATA%\CrystalReportVersioner\report-index.csv).
    /// The more reports are scanned, the more searches can be answered from the index without opening .rpt files.
    /// </summary>
    public sealed class ReportIndex
    {
        private static readonly string[] Columns = { "Path", "FileSize", "LastWriteUtc", "IndexedUtc", "Hits", "LearnedTerms", "Keywords", "Summary", "Error" };
        private readonly Dictionary<string, ReportIndexEntry> _entries = new Dictionary<string, ReportIndexEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new object();

        public string FilePath { get; private set; }

        public static string DefaultPath
        {
            get
            {
                return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "CrystalReportVersioner", "report-index.csv");
            }
        }

        public int Count { get { lock (_sync) return _entries.Count; } }

        public static ReportIndex Load(string path)
        {
            var idx = new ReportIndex { FilePath = string.IsNullOrWhiteSpace(path) ? DefaultPath : Environment.ExpandEnvironmentVariables(path.Trim()) };
            if (!File.Exists(idx.FilePath)) return idx;
            var rows = ReadCsv(File.ReadAllText(idx.FilePath, Encoding.UTF8));
            if (rows.Count == 0) return idx;
            var header = rows[0];
            Func<List<string>, string, string> col = (r, name) =>
            {
                var i = header.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));
                return i >= 0 && i < r.Count ? r[i] : string.Empty;
            };
            foreach (var r in rows.Skip(1))
            {
                var p = col(r, "Path");
                if (string.IsNullOrWhiteSpace(p)) continue;
                long size; int hits; DateTime lw, ix;
                long.TryParse(col(r, "FileSize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                int.TryParse(col(r, "Hits"), NumberStyles.Integer, CultureInfo.InvariantCulture, out hits);
                DateTime.TryParse(col(r, "LastWriteUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out lw);
                DateTime.TryParse(col(r, "IndexedUtc"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out ix);
                idx._entries[p] = new ReportIndexEntry
                {
                    Path = p, FileSize = size, LastWriteUtc = lw, IndexedUtc = ix, Hits = hits,
                    LearnedTerms = col(r, "LearnedTerms"), Keywords = col(r, "Keywords"),
                    Summary = col(r, "Summary"), Error = col(r, "Error")
                };
            }
            return idx;
        }

        public void Save()
        {
            List<ReportIndexEntry> list;
            lock (_sync) list = _entries.Values.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ToList();
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", Columns));
            foreach (var e in list)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Esc(e.Path), e.FileSize.ToString(CultureInfo.InvariantCulture),
                    e.LastWriteUtc.ToString("o", CultureInfo.InvariantCulture), e.IndexedUtc.ToString("o", CultureInfo.InvariantCulture),
                    e.Hits.ToString(CultureInfo.InvariantCulture), Esc(e.LearnedTerms), Esc(e.Keywords), Esc(e.Summary), Esc(e.Error)
                }));
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        public ReportIndexEntry Get(string path)
        {
            lock (_sync)
            {
                ReportIndexEntry e;
                return _entries.TryGetValue(path, out e) ? e : null;
            }
        }

        public ReportIndexEntry GetOrCreate(string path)
        {
            lock (_sync)
            {
                ReportIndexEntry e;
                if (!_entries.TryGetValue(path, out e)) { e = new ReportIndexEntry { Path = path }; _entries[path] = e; }
                return e;
            }
        }

        public List<ReportIndexEntry> All()
        {
            lock (_sync) return _entries.Values.ToList();
        }

        /// <summary>Records that <paramref name="path"/> was relevant for the query so future searches rank it higher.</summary>
        public void Learn(string path, IEnumerable<string> queryTokens)
        {
            var e = GetOrCreate(path);
            lock (_sync)
            {
                e.Hits++;
                var terms = new HashSet<string>((e.LearnedTerms ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
                foreach (var t in queryTokens) terms.Add(t);
                e.LearnedTerms = string.Join(" ", terms.Take(200));
            }
        }

        private static string Esc(string s)
        {
            s = s ?? string.Empty;
            if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return s;
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private static List<List<string>> ReadCsv(string text)
        {
            var rows = new List<List<string>>();
            var row = new List<string>();
            var cell = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else cell.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
                else if (c == '\r') { }
                else if (c == '\n') { row.Add(cell.ToString()); cell.Clear(); rows.Add(row); row = new List<string>(); }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            return rows;
        }
    }
}
