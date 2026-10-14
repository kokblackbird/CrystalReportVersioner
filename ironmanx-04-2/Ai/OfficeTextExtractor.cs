using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace ironmanx_04_2.Ai
{
    /// <summary>
    /// Text extraction for binary office formats used as agent reference documents.
    /// .xlsx is parsed directly (no Office needed). .xls, .doc and .pdf use late-bound Microsoft Excel / Word
    /// automation on a private STA thread (Word opens PDFs via PDF Reflow, Word 2013+).
    /// </summary>
    internal static class OfficeTextExtractor
    {
        private const int MaxSheetRows = 5000;
        private const int MaxSheetCols = 100;
        private const int ComTimeoutSeconds = 120;

        // ── .xlsx (Open XML) ────────────────────────────────────────────────

        public static string ExtractXlsx(string path)
        {
            XNamespace m = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XNamespace r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            XNamespace pr = "http://schemas.openxmlformats.org/package/2006/relationships";

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                var shared = new List<string>();
                var ss = zip.GetEntry("xl/sharedStrings.xml");
                if (ss != null)
                {
                    var x = LoadXml(ss);
                    foreach (var si in x.Root.Elements(m + "si"))
                        shared.Add(string.Concat(si.Descendants(m + "t").Select(t => t.Value)));
                }

                var wb = LoadXml(zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Not a valid .xlsx file."));
                var relsEntry = zip.GetEntry("xl/_rels/workbook.xml.rels");
                var rels = relsEntry == null ? new Dictionary<string, string>() :
                    LoadXml(relsEntry).Root.Elements(pr + "Relationship")
                        .ToDictionary(e => (string)e.Attribute("Id"), e => (string)e.Attribute("Target"));

                var sb = new StringBuilder();
                foreach (var sheet in wb.Descendants(m + "sheet"))
                {
                    var name = (string)sheet.Attribute("name");
                    var rid = (string)sheet.Attribute(r + "id");
                    string target;
                    if (rid == null || !rels.TryGetValue(rid, out target)) continue;
                    target = target.StartsWith("/") ? target.TrimStart('/') : "xl/" + target;
                    var entry = zip.GetEntry(target.Replace("\\", "/"));
                    if (entry == null) continue;

                    sb.AppendLine("### Sheet: " + name);
                    var xs = LoadXml(entry);
                    int rows = 0;
                    foreach (var row in xs.Descendants(m + "row"))
                    {
                        if (++rows > MaxSheetRows) { sb.AppendLine("... (sheet truncated)"); break; }
                        var cells = new List<string>();
                        foreach (var c in row.Elements(m + "c"))
                        {
                            var col = ColumnIndex((string)c.Attribute("r"));
                            if (col >= MaxSheetCols) continue;
                            while (col >= 0 && cells.Count < col) cells.Add(string.Empty);
                            cells.Add(CellText(c, m, shared));
                        }
                        while (cells.Count > 0 && cells[cells.Count - 1].Length == 0) cells.RemoveAt(cells.Count - 1);
                        if (cells.Count > 0) sb.AppendLine(string.Join("\t", cells));
                    }
                    sb.AppendLine();
                }
                return sb.ToString();
            }
        }

        private static string CellText(XElement c, XNamespace m, List<string> shared)
        {
            var type = (string)c.Attribute("t");
            if (type == "inlineStr") return string.Concat(c.Descendants(m + "t").Select(t => t.Value));
            var v = (string)c.Element(m + "v");
            if (v == null) return string.Empty;
            if (type == "s")
            {
                int idx;
                return int.TryParse(v, out idx) && idx >= 0 && idx < shared.Count ? shared[idx] : string.Empty;
            }
            if (type == "b") return v == "1" ? "TRUE" : "FALSE";
            return v;
        }

        private static int ColumnIndex(string cellRef)
        {
            if (string.IsNullOrEmpty(cellRef)) return -1;
            int n = 0;
            foreach (var ch in cellRef)
            {
                if (!char.IsLetter(ch)) break;
                n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            }
            return n - 1;
        }

        private static XDocument LoadXml(ZipArchiveEntry e)
        {
            using (var s = e.Open()) return XDocument.Load(s);
        }

        // ── .doc / .pdf via Word, .xls via Excel ────────────────────────────

        public static string ExtractWithWord(string path)
        {
            return RunSta(() =>
            {
                object word = null, docs = null, doc = null, content = null;
                try
                {
                    word = CreateCom("Word.Application", "Microsoft Word is required to read .doc and .pdf files.");
                    Set(word, "Visible", false);
                    Set(word, "DisplayAlerts", 0); // wdAlertsNone
                    docs = Get(word, "Documents");
                    // Open(FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, ..., Visible)
                    doc = Call(docs, "Open", path, false, true, false, "", "", false, "", "", Type.Missing, Type.Missing, false);
                    content = Get(doc, "Content");
                    var text = Convert.ToString(Get(content, "Text")) ?? string.Empty;
                    return text.Replace("\r\a", "\t").Replace('\a', '\t').Replace("\r", "\n").Replace('\v', '\n');
                }
                finally
                {
                    Release(content);
                    if (doc != null) { try { Call(doc, "Close", false); } catch { } }
                    Release(doc);
                    Release(docs);
                    if (word != null) { try { Call(word, "Quit", false); } catch { } }
                    Release(word);
                }
            });
        }

        public static string ExtractWithExcel(string path)
        {
            return RunSta(() =>
            {
                object excel = null, books = null, book = null, sheets = null;
                try
                {
                    excel = CreateCom("Excel.Application", "Microsoft Excel is required to read .xls files (or save the file as .xlsx).");
                    Set(excel, "Visible", false);
                    Set(excel, "DisplayAlerts", false);
                    books = Get(excel, "Workbooks");
                    // Open(Filename, UpdateLinks, ReadOnly)
                    book = Call(books, "Open", path, 0, true);
                    sheets = Get(book, "Worksheets");
                    var count = Convert.ToInt32(Get(sheets, "Count"));
                    var sb = new StringBuilder();
                    for (int i = 1; i <= count; i++)
                    {
                        object sheet = null, used = null;
                        try
                        {
                            sheet = GetIndexed(sheets, i);
                            sb.AppendLine("### Sheet: " + Get(sheet, "Name"));
                            used = Get(sheet, "UsedRange");
                            var values = Get(used, "Value2");
                            var arr = values as object[,];
                            if (arr != null)
                            {
                                int r0 = arr.GetLowerBound(0), r1 = Math.Min(arr.GetUpperBound(0), r0 + MaxSheetRows - 1);
                                int c0 = arr.GetLowerBound(1), c1 = Math.Min(arr.GetUpperBound(1), c0 + MaxSheetCols - 1);
                                for (int r = r0; r <= r1; r++)
                                {
                                    var cells = new List<string>();
                                    for (int c = c0; c <= c1; c++) cells.Add(Convert.ToString(arr[r, c]) ?? string.Empty);
                                    while (cells.Count > 0 && cells[cells.Count - 1].Length == 0) cells.RemoveAt(cells.Count - 1);
                                    if (cells.Count > 0) sb.AppendLine(string.Join("\t", cells));
                                }
                                if (arr.GetUpperBound(0) > r1) sb.AppendLine("... (sheet truncated)");
                            }
                            else if (values != null)
                            {
                                sb.AppendLine(Convert.ToString(values));
                            }
                            sb.AppendLine();
                        }
                        finally { Release(used); Release(sheet); }
                    }
                    return sb.ToString();
                }
                finally
                {
                    Release(sheets);
                    if (book != null) { try { Call(book, "Close", false); } catch { } }
                    Release(book);
                    Release(books);
                    if (excel != null) { try { Call(excel, "Quit"); } catch { } }
                    Release(excel);
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            });
        }

        // ── COM helpers ─────────────────────────────────────────────────────

        private static T RunSta<T>(Func<T> work)
        {
            T result = default(T);
            Exception error = null;
            var t = new Thread(() =>
            {
                try { result = work(); }
                catch (TargetInvocationException ex) { error = ex.InnerException ?? ex; }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(TimeSpan.FromSeconds(ComTimeoutSeconds)))
                throw new TimeoutException("Timed out reading the document through Office.");
            if (error != null) throw new InvalidOperationException(error.Message, error);
            return result;
        }

        private static object CreateCom(string progId, string missingMessage)
        {
            var type = Type.GetTypeFromProgID(progId);
            if (type == null) throw new NotSupportedException(missingMessage);
            return Activator.CreateInstance(type);
        }

        private static object Get(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        private static object GetIndexed(object target, object index)
        {
            return target.GetType().InvokeMember("Item", BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, target, new[] { index });
        }

        private static void Set(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });
        }

        private static object Call(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }

        private static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) { try { Marshal.FinalReleaseComObject(o); } catch { } }
        }
    }
}
