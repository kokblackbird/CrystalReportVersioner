using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ironmanx_04_2.Ai
{
    /// <summary>
    /// User-supplied reference documents (specs, SQL, style guides, sample formulas, etc.) that the agent
    /// can list, search and read. Small documents are also summarized into the system prompt.
    /// Supported: plain text formats (.txt .md .sql .json .xml .csv .cs .vb .ini .log .html ...) and .docx.
    /// </summary>
    public sealed class ResourceLibrary
    {
        public const int MaxDocumentChars = 400000;
        public const int InlineBudgetChars = 12000;
        private const int ReadChunkChars = 8000;

        private static readonly HashSet<string> TextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".sql", ".json", ".xml", ".csv", ".tsv", ".cs", ".vb", ".ini", ".cfg",
            ".log", ".html", ".htm", ".yaml", ".yml", ".rtf", ".bas", ".frm", ".ps1", ".bat", ".cmd"
        };

        public sealed class Doc
        {
            public string Name { get; set; }
            public string Path { get; set; }
            public string Text { get; set; }
            public string Error { get; set; }
            public bool Truncated { get; set; }
        }

        private readonly List<Doc> _docs = new List<Doc>();
        private readonly AiSettings _settings;

        public ResourceLibrary(AiSettings settings)
        {
            _settings = settings;
            foreach (var p in settings.ResourceFiles ?? new List<string>()) Load(p);
        }

        public IReadOnlyList<Doc> Documents { get { return _docs; } }

        public static bool IsSupported(string path)
        {
            var ext = System.IO.Path.GetExtension(path) ?? string.Empty;
            return TextExtensions.Contains(ext) || OfficeExtensions.Contains(ext);
        }

        private static readonly HashSet<string> OfficeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".docx", ".doc", ".pdf", ".xlsx", ".xlsm", ".xls"
        };

        public static string FileDialogFilter
        {
            get { return "Reference documents|*.txt;*.md;*.sql;*.json;*.xml;*.csv;*.tsv;*.docx;*.doc;*.pdf;*.xlsx;*.xlsm;*.xls;*.html;*.htm;*.yaml;*.yml;*.ini;*.cs;*.vb;*.log;*.rtf|All files (*.*)|*.*"; }
        }

        private void Load(string path)
        {
            var doc = new Doc { Path = path, Name = UniqueName(System.IO.Path.GetFileName(path)) };
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("File not found.");
                var text = ExtractText(path);
                if (text.Length > MaxDocumentChars) { text = text.Substring(0, MaxDocumentChars); doc.Truncated = true; }
                doc.Text = text;
            }
            catch (Exception ex)
            {
                doc.Text = string.Empty;
                doc.Error = ex.Message;
            }
            _docs.Add(doc);
        }

        private string UniqueName(string name)
        {
            var n = name; int i = 2;
            while (_docs.Any(d => d.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) n = name + " (" + i++ + ")";
            return n;
        }

        private static string ExtractText(string path)
        {
            var ext = System.IO.Path.GetExtension(path) ?? string.Empty;
            if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase)) return ExtractDocx(path);
            if (ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)) return OfficeTextExtractor.ExtractXlsx(path);
            if (ext.Equals(".xls", StringComparison.OrdinalIgnoreCase)) return OfficeTextExtractor.ExtractWithExcel(path);
            if (ext.Equals(".doc", StringComparison.OrdinalIgnoreCase) || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                var t = OfficeTextExtractor.ExtractWithWord(path);
                if (string.IsNullOrWhiteSpace(t)) throw new InvalidDataException("No text found (the PDF may be scanned images).");
                return t;
            }
            if (!TextExtensions.Contains(ext)) throw new NotSupportedException("Unsupported file type " + ext + ".");
            var text = File.ReadAllText(path);
            if (ext.Equals(".rtf", StringComparison.OrdinalIgnoreCase)) text = StripRtf(text);
            if (ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                text = System.Net.WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(text, @"(?is)<(script|style).*?</\1>", " "), "<[^>]+>", " "));
            return text;
        }

        private static string ExtractDocx(string path)
        {
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) throw new InvalidDataException("Not a valid .docx file.");
                XDocument xml;
                using (var s = entry.Open()) xml = XDocument.Load(s);
                var sb = new StringBuilder();
                foreach (var p in xml.Descendants(w + "p"))
                {
                    foreach (var node in p.Descendants())
                    {
                        if (node.Name == w + "t") sb.Append(node.Value);
                        else if (node.Name == w + "tab") sb.Append('\t');
                        else if (node.Name == w + "br") sb.AppendLine();
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }
        }

        private static string StripRtf(string rtf)
        {
            var s = Regex.Replace(rtf, @"\\par[d]?", "\n");
            s = Regex.Replace(s, @"\{\\\*[^{}]*\}", string.Empty);
            s = Regex.Replace(s, @"\\[a-zA-Z]+-?\d* ?", string.Empty);
            return s.Replace("{", string.Empty).Replace("}", string.Empty);
        }

        private string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
                if (_settings.IsSensitive(lines[i])) lines[i] = "<line redacted: sensitive keyword>";
            return string.Join("\n", lines);
        }

        private Doc Find(string name)
        {
            var d = _docs.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                 ?? _docs.FirstOrDefault(x => x.Name.IndexOf(name ?? string.Empty, StringComparison.OrdinalIgnoreCase) >= 0);
            if (d == null) throw new ArgumentException("No resource document named '" + name + "'. Call list_resources.");
            if (d.Error != null) throw new InvalidOperationException("Resource '" + d.Name + "' could not be loaded: " + d.Error);
            return d;
        }

        // ── Prompt + tools ───────────────────────────────────────────────────

        /// <summary>Text appended to the system prompt: inlines small documents, lists the rest.</summary>
        public string BuildPromptSection()
        {
            if (_docs.Count == 0) return null;
            var sb = new StringBuilder();
            sb.AppendLine("Reference documents supplied by the user (follow them; they describe what the user is trying to accomplish, standards, and business rules):");
            int budget = InlineBudgetChars;
            foreach (var d in _docs)
            {
                if (d.Error != null) { sb.AppendLine("- " + d.Name + " (failed to load: " + d.Error + ")"); continue; }
                if (d.Text.Length <= budget)
                {
                    sb.AppendLine("=== " + d.Name + " ===");
                    sb.AppendLine(Redact(d.Text).Trim());
                    sb.AppendLine("=== end " + d.Name + " ===");
                    budget -= d.Text.Length;
                }
                else
                {
                    sb.AppendLine("- " + d.Name + " (" + d.Text.Length + " chars, not inlined — use search_resources / read_resource)");
                }
            }
            return sb.ToString();
        }

        public object List()
        {
            return new Dictionary<string, object>
            {
                { "documents", _docs.Select(d => (object)new Dictionary<string, object>
                    {
                        { "name", d.Name },
                        { "chars", d.Text == null ? 0 : d.Text.Length },
                        { "truncated", d.Truncated },
                        { "error", d.Error }
                    }).ToList() }
            };
        }

        public object Read(string name, int offset)
        {
            var d = Find(name);
            if (offset < 0) offset = 0;
            if (offset >= d.Text.Length) return new Dictionary<string, object> { { "name", d.Name }, { "text", string.Empty }, { "eof", true } };
            var len = Math.Min(ReadChunkChars, d.Text.Length - offset);
            return new Dictionary<string, object>
            {
                { "name", d.Name },
                { "offset", offset },
                { "nextOffset", offset + len < d.Text.Length ? (object)(offset + len) : null },
                { "totalChars", d.Text.Length },
                { "text", Redact(d.Text.Substring(offset, len)) }
            };
        }

        public object Search(string query, int maxHits)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");
            if (maxHits <= 0) maxHits = 20;
            var terms = query.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var hits = new List<object>();
            foreach (var d in _docs.Where(x => x.Error == null))
            {
                var lines = d.Text.Split('\n');
                int pos = 0;
                for (int i = 0; i < lines.Length && hits.Count < maxHits; i++)
                {
                    var line = lines[i];
                    int score = terms.Count(t => line.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (score > 0 && (score == terms.Length || terms.Length > 2 && score * 2 >= terms.Length))
                    {
                        var from = Math.Max(0, i - 2); var to = Math.Min(lines.Length - 1, i + 2);
                        hits.Add(new Dictionary<string, object>
                        {
                            { "document", d.Name },
                            { "line", i + 1 },
                            { "offset", pos },
                            { "snippet", Redact(string.Join("\n", lines.Skip(from).Take(to - from + 1)).Trim()) }
                        });
                    }
                    pos += line.Length + 1;
                }
            }
            return new Dictionary<string, object> { { "query", query }, { "hits", hits } };
        }
    }
}
