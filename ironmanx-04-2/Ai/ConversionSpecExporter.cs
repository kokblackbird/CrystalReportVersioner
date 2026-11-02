using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrystalDecisions.ReportAppServer.DataDefModel;
using CrystalDecisions.ReportAppServer.ReportDefModel;

namespace ironmanx_04_2.Ai
{
    /// <summary>
    /// Builds a "convert this Crystal report to C#" specification (Markdown + JSON) that can be handed to
    /// another coding agent (e.g. GitHub Copilot in Visual Studio). Contains formulas, selection logic,
    /// tables, fields, joins, subreport links, grouping/sorting, parameters and layout. No row data.
    /// </summary>
    public sealed partial class ReportAgentTools
    {
        private string _conversionNotes;
        private readonly List<string> _userRequirements = new List<string>();
        private string _aiIdeas;

        /// <summary>Adds the user's own conversion requirements (typed in the agent window) to the exported spec.</summary>
        public void AddConversionRequirement(string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) _userRequirements.Add(text.Trim());
        }

        /// <summary>Writes REPORT_SPEC.md, report-spec.json and copilot-prompt.txt into the folder. Returns the .md path.</summary>
        public System.Threading.Tasks.Task<string> ExportConversionSpecAsync(string folder)
        {
            return _worker.Invoke(() =>
            {
                Directory.CreateDirectory(folder);
                var spec = BuildSpecModel();
                var md = Path.Combine(folder, "REPORT_SPEC.md");
                File.WriteAllText(md, BuildSpecMarkdown(spec), Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder, "report-spec.json"), PrettyJson(Json(spec)), Encoding.UTF8);
                File.WriteAllText(Path.Combine(folder, "copilot-prompt.txt"), CopilotPrompt(_userRequirements) + (string.IsNullOrWhiteSpace(_aiIdeas) ? "" : "\n\nAlso consider the 'Additional ideas' section in REPORT_SPEC.md (optional suggestions)."), Encoding.UTF8);
                return md;
            });
        }

        /// <summary>Returns the structured spec, the full Markdown spec and the compact JSON (for batch export).</summary>
        public System.Threading.Tasks.Task<Tuple<Dictionary<string, object>, string, string>> GetSpecAsync()
        {
            return _worker.Invoke(() =>
            {
                var spec = BuildSpecModel();
                return Tuple.Create(spec, BuildSpecMarkdown(spec), Json(spec));
            });
        }

        /// <summary>Formats every formula and selection formula of the spec as Markdown.</summary>
        public static string FormulasMarkdown(Dictionary<string, object> spec)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Formulas: " + spec["reportName"]);
            sb.AppendLine();
            var scopes = new List<Dictionary<string, object>> { (Dictionary<string, object>)spec["main"] };
            foreach (Dictionary<string, object> s in (List<object>)spec["subreports"]) scopes.Add(s);
            foreach (var s in scopes)
            {
                sb.AppendLine("## " + (Convert.ToString(s["name"]) == "Main" ? "Main report" : "Subreport: " + s["name"]));
                sb.AppendLine();
                sb.AppendLine("### Record selection");
                Code(sb, s["recordSelection"] as string, "crystal");
                sb.AppendLine("### Group selection");
                Code(sb, s["groupSelection"] as string, "crystal");
                var formulas = (List<object>)s["formulas"];
                if (formulas.Count == 0) { sb.AppendLine("_No formulas._"); sb.AppendLine(); }
                foreach (Dictionary<string, object> f in formulas)
                {
                    sb.AppendLine("### " + f["crystalRef"] + "  (" + f["resultType"] + ", " + f["syntax"] + ")");
                    Code(sb, f["text"] as string, "crystal");
                }
            }
            return sb.ToString();
        }

        private object SetConversionNotes(Dictionary<string, object> a)
        {
            var req = S(a, "user_requirement");
            if (!string.IsNullOrWhiteSpace(req)) AddConversionRequirement(req);
            var ideas = S(a, "ideas");
            if (!string.IsNullOrWhiteSpace(ideas)) _aiIdeas = string.IsNullOrEmpty(_aiIdeas) ? ideas : _aiIdeas + "\n\n" + ideas;
            if (!a.ContainsKey("markdown")) return Ok("Requirement/ideas stored.");
            var text = ReqAllowEmpty(a, "markdown");
            var append = B(a, "append");
            _conversionNotes = append && !string.IsNullOrEmpty(_conversionNotes) ? _conversionNotes + "\n\n" + text : text;
            return Ok("Conversion notes stored (" + _conversionNotes.Length + " chars). They will be included when the user clicks 'Export C# Spec...'.");
        }

        private object GetConversionSpec()
        {
            return BuildSpecModel();
        }

        // ── model ────────────────────────────────────────────────────────────

        private Dictionary<string, object> BuildSpecModel()
        {
            var spec = new Dictionary<string, object>();
            spec["reportName"] = Path.GetFileNameWithoutExtension(SourcePath);
            spec["sourceFile"] = Path.GetFileName(SourcePath);
            spec["exportedUtc"] = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);
            spec["units"] = "Layout positions are twips (1440 per inch, 20 per point).";
            spec["main"] = ScopeSpec(Scope((string)null));
            var subs = new List<object>();
            foreach (var n in SubreportNames())
            {
                var s = ScopeSpec(Scope(n));
                s["linksToMain"] = SubreportLinkSpec(n);
                subs.Add(s);
            }
            spec["subreports"] = subs;
            if (!string.IsNullOrWhiteSpace(_conversionNotes)) spec["aiConversionNotes"] = _conversionNotes;
            if (_userRequirements.Count > 0) spec["userRequirements"] = _userRequirements.ToList();
            if (!string.IsNullOrWhiteSpace(_aiIdeas)) spec["aiImplementationIdeas"] = _aiIdeas;
            return spec;
        }

        private Dictionary<string, object> ScopeSpec(ReportScope s)
        {
            var dd = s.DataDef.DataDefinition;
            var d = new Dictionary<string, object>();
            d["name"] = s.Name ?? "Main";

            var tables = new List<object>();
            foreach (ISCRTable t in s.Database.Database.Tables)
            {
                var fields = new List<object>();
                if (t.DataFields != null)
                    foreach (ISCRField f in t.DataFields)
                        fields.Add(new Dictionary<string, object> { { "name", f.Name }, { "crystalRef", f.FormulaForm }, { "type", CleanType(f.Type.ToString()) }, { "csharpType", CSharpType(f.Type.ToString()) } });
                var td = new Dictionary<string, object>
                {
                    { "alias", t.Alias }, { "name", t.Name }, { "qualifiedName", t.QualifiedName }, { "fields", fields }
                };
                td["connection"] = DescribeConnection(t);
                var cmd = t as CommandTable;
                if (cmd != null) td["sqlCommand"] = SafeGet(() => cmd.CommandText);
                tables.Add(td);
            }
            d["tables"] = tables;
            d["joins"] = DescribeLinks(s);

            var parameters = new List<object>();
            foreach (ISCRField p in dd.ParameterFields)
                parameters.Add(new Dictionary<string, object> { { "name", p.Name }, { "crystalRef", p.FormulaForm }, { "type", CleanType(p.Type.ToString()) }, { "csharpType", CSharpType(p.Type.ToString()) } });
            d["parameters"] = parameters;

            d["recordSelection"] = SafeGet(() => s.DataDef.RecordFilterController.GetFormulaText());
            d["groupSelection"] = SafeGet(() => s.DataDef.GroupFilterController.GetFormulaText());

            var formulas = new List<object>();
            foreach (ISCRField f in dd.FormulaFields)
            {
                var ff = f as ISCRFormulaField;
                formulas.Add(new Dictionary<string, object>
                {
                    { "name", f.Name }, { "crystalRef", f.FormulaForm },
                    { "resultType", CleanType(f.Type.ToString()) },
                    { "syntax", ff != null ? (ff.Syntax == CrFormulaSyntaxEnum.crFormulaSyntaxBasic ? "Basic" : "Crystal") : null },
                    { "text", ff != null ? ff.Text : null }
                });
            }
            d["formulas"] = formulas;

            var groups = new List<object>();
            foreach (Group g in dd.Groups)
                groups.Add(g.ConditionField != null ? g.ConditionField.FormulaForm : null);
            d["groups"] = groups;

            var sorts = new List<object>();
            foreach (ISCRSort so in dd.Sorts)
                sorts.Add(new Dictionary<string, object> { { "field", so.SortField != null ? so.SortField.FormulaForm : null }, { "direction", so.Direction.ToString().Replace("crSortDirection", "") } });
            d["sorts"] = sorts;

            var summaries = new List<object>();
            foreach (ISCRField sf in dd.SummaryFields) summaries.Add(new Dictionary<string, object> { { "ref", sf.FormulaForm }, { "description", SafeGet(() => sf.Description) } });
            d["summaries"] = summaries;

            var rts = new List<object>();
            foreach (ISCRField rt in dd.RunningTotalFields) rts.Add(new Dictionary<string, object> { { "ref", rt.FormulaForm }, { "description", SafeGet(() => rt.Description) } });
            d["runningTotals"] = rts;

            var areas = new List<object>();
            foreach (ISCRArea area in s.ReportDef.ReportDefinition.Areas)
            {
                var sections = new List<object>();
                foreach (Section sec in area.Sections)
                {
                    var objects = new List<object>();
                    foreach (ISCRReportObject o in sec.ReportObjects) objects.Add(DescribeObject(o));
                    sections.Add(new Dictionary<string, object>
                    {
                        { "name", sec.Name }, { "height", sec.Height }, { "width", sec.Width },
                        { "suppressed", sec.Format != null && sec.Format.EnableSuppress },
                        { "objects", objects }
                    });
                }
                areas.Add(new Dictionary<string, object> { { "kind", area.Kind.ToString().Replace("crAreaSectionKind", "") }, { "name", area.Name }, { "sections", sections } });
            }
            d["layout"] = areas;
            return d;
        }

        private List<object> SubreportLinkSpec(string name)
        {
            var list = new List<object>();
            try
            {
                var links = _rcd.SubreportController.GetSubreportLinks(name);
                if (links != null)
                    foreach (SubreportLink l in links)
                        list.Add(new Dictionary<string, object> { { "mainField", l.MainReportFieldName }, { "subreportField", l.SubreportFieldName }, { "parameter", l.LinkedParameterName } });
            }
            catch { }
            return list;
        }

        private static string DescribeConnection(ISCRTable t)
        {
            try
            {
                var ci = t.ConnectionInfo;
                if (ci == null || ci.Attributes == null) return null;
                var parts = new List<string>();
                foreach (var key in new[] { "QE_DatabaseType", "QE_DatabaseName", "QE_ServerDescription", "Database DLL" })
                {
                    var v = SafeGet(() => Convert.ToString(ci.Attributes.get_StringValue(key)));
                    if (!string.IsNullOrWhiteSpace(v)) parts.Add(key + "=" + v);
                }
                var all = string.Join("; ", parts);
                if (all.IndexOf("FoxPro", StringComparison.OrdinalIgnoreCase) >= 0 || all.IndexOf(".dbf", StringComparison.OrdinalIgnoreCase) >= 0 || all.IndexOf(".dbc", StringComparison.OrdinalIgnoreCase) >= 0)
                    all = "Visual FoxPro (ODBC); " + all;
                return all;
            }
            catch { return null; }
        }

        private static string CleanType(string t)
        {
            return t.Replace("crFieldValueType", "").Replace("Field", "");
        }

        private static string CSharpType(string t)
        {
            t = CleanType(t);
            switch (t)
            {
                case "Int8s": case "Int8u": case "Int16s": case "Int16u": case "Int32s": return "int";
                case "Int32u": return "long";
                case "Number": return "double";
                case "Currency": return "decimal";
                case "Boolean": return "bool";
                case "Date": case "DateTime": return "DateTime";
                case "Time": return "TimeSpan";
                case "String": case "Memo": return "string";
                case "Blob": case "Bitmap": case "Picture": return "byte[]";
                default: return "object";
            }
        }

        // ── markdown ─────────────────────────────────────────────────────────

        private string BuildSpecMarkdown(Dictionary<string, object> spec)
        {
            var sb = new StringBuilder();
            var name = (string)spec["reportName"];
            sb.AppendLine("# Report conversion spec: " + name);
            sb.AppendLine();
            sb.AppendLine("> Source: `" + spec["sourceFile"] + "` (SAP Crystal Reports). Exported " + spec["exportedUtc"] + ".");
            sb.AppendLine("> Goal: re-implement this report as plain C# with **no Crystal Reports dependency**. The full machine-readable spec is in `report-spec.json`.");
            sb.AppendLine();
            if (spec.ContainsKey("userRequirements"))
            {
                sb.AppendLine("## User requirements (highest priority - these override the defaults below)");
                sb.AppendLine();
                foreach (var r in (List<string>)spec["userRequirements"])
                {
                    sb.AppendLine("- " + r.Replace("\r", "").Replace("\n", "\n  "));
                }
                sb.AppendLine();
            }
            if (spec.ContainsKey("aiImplementationIdeas"))
            {
                sb.AppendLine("## Additional ideas for achieving the requirements (AI suggestions - optional, user requirements win)");
                sb.AppendLine();
                sb.AppendLine((string)spec["aiImplementationIdeas"]);
                sb.AppendLine();
            }
            sb.AppendLine("## Instructions for the coding agent");
            sb.AppendLine();
            sb.AppendLine(Instructions());
            sb.AppendLine();
            sb.AppendLine("## Crystal → C# cheat sheet");
            sb.AppendLine();
            sb.AppendLine(CheatSheet());
            sb.AppendLine();

            if (spec.ContainsKey("aiConversionNotes"))
            {
                sb.AppendLine("## AI analyst notes (translated logic / caveats)");
                sb.AppendLine();
                sb.AppendLine((string)spec["aiConversionNotes"]);
                sb.AppendLine();
            }

            AppendScope(sb, (Dictionary<string, object>)spec["main"], "Main report", 2);
            foreach (Dictionary<string, object> sub in (List<object>)spec["subreports"])
                AppendScope(sb, sub, "Subreport: " + sub["name"], 2);
            return sb.ToString();
        }

        private static void AppendScope(StringBuilder sb, Dictionary<string, object> s, string title, int level)
        {
            var h = new string('#', level);
            sb.AppendLine(h + " " + title);
            sb.AppendLine();

            object lm;
            if (s.TryGetValue("linksToMain", out lm) && ((List<object>)lm).Count > 0)
            {
                sb.AppendLine(h + "# Link to main report (runs once per main-report row / group)");
                sb.AppendLine();
                sb.AppendLine("| Main field | Subreport field | Parameter |");
                sb.AppendLine("|---|---|---|");
                foreach (Dictionary<string, object> l in (List<object>)lm)
                    sb.AppendLine("| `" + l["mainField"] + "` | `" + l["subreportField"] + "` | `" + l["parameter"] + "` |");
                sb.AppendLine();
            }

            sb.AppendLine(h + "# Data sources");
            sb.AppendLine();
            foreach (Dictionary<string, object> t in (List<object>)s["tables"])
            {
                sb.AppendLine("**" + t["alias"] + "** (`" + t["qualifiedName"] + "`)" + (t["connection"] != null ? " - connection: " + t["connection"] : ""));
                object sql;
                if (t.TryGetValue("sqlCommand", out sql) && sql != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("SQL command:");
                    sb.AppendLine("```sql");
                    sb.AppendLine(Convert.ToString(sql));
                    sb.AppendLine("```");
                }
                sb.AppendLine();
                sb.AppendLine("| Field | Crystal type | C# type |");
                sb.AppendLine("|---|---|---|");
                foreach (Dictionary<string, object> f in (List<object>)t["fields"])
                    sb.AppendLine("| `" + f["crystalRef"] + "` | " + f["type"] + " | `" + f["csharpType"] + "` |");
                sb.AppendLine();
            }

            var joins = (List<object>)s["joins"];
            sb.AppendLine(h + "# Joins (relational links)");
            sb.AppendLine();
            if (joins.Count == 0) sb.AppendLine("_None._");
            foreach (Dictionary<string, object> j in joins)
            {
                var from = (List<string>)j["fromFields"];
                var to = (List<string>)j["toFields"];
                var on = new List<string>();
                for (int i = 0; i < Math.Min(from.Count, to.Count); i++) on.Add(j["from"] + "." + from[i] + " = " + j["to"] + "." + to[i]);
                sb.AppendLine("- `" + j["from"] + "` **" + j["join"] + "** JOIN `" + j["to"] + "` ON " + string.Join(" AND ", on));
            }
            sb.AppendLine();

            var parameters = (List<object>)s["parameters"];
            if (parameters.Count > 0)
            {
                sb.AppendLine(h + "# Parameters");
                sb.AppendLine();
                foreach (Dictionary<string, object> p in parameters)
                    sb.AppendLine("- `" + p["crystalRef"] + "` : " + p["type"] + " → `" + p["csharpType"] + "`");
                sb.AppendLine();
            }

            sb.AppendLine(h + "# Record selection (WHERE, before grouping)");
            sb.AppendLine();
            Code(sb, s["recordSelection"] as string, "crystal");
            sb.AppendLine(h + "# Group selection (filter after grouping)");
            sb.AppendLine();
            Code(sb, s["groupSelection"] as string, "crystal");

            sb.AppendLine(h + "# Grouping and sorting");
            sb.AppendLine();
            int gi = 1;
            foreach (var g in (List<object>)s["groups"]) sb.AppendLine((gi++) + ". Group by `" + g + "`");
            foreach (Dictionary<string, object> so in (List<object>)s["sorts"]) sb.AppendLine("- Sort `" + so["field"] + "` " + so["direction"]);
            sb.AppendLine();

            var summaries = (List<object>)s["summaries"];
            var rts = (List<object>)s["runningTotals"];
            if (summaries.Count + rts.Count > 0)
            {
                sb.AppendLine(h + "# Summaries and running totals");
                sb.AppendLine();
                foreach (Dictionary<string, object> x in summaries) sb.AppendLine("- Summary `" + x["ref"] + "` " + x["description"]);
                foreach (Dictionary<string, object> x in rts) sb.AppendLine("- Running total `" + x["ref"] + "` " + x["description"]);
                sb.AppendLine();
            }

            sb.AppendLine(h + "# Formulas");
            sb.AppendLine();
            var formulas = (List<object>)s["formulas"];
            if (formulas.Count == 0) sb.AppendLine("_None._");
            foreach (Dictionary<string, object> f in formulas)
            {
                sb.AppendLine("**`" + f["crystalRef"] + "`** → result " + f["resultType"] + " (" + f["syntax"] + " syntax)");
                Code(sb, f["text"] as string, "crystal");
            }

            sb.AppendLine(h + "# Layout (what each band prints)");
            sb.AppendLine();
            foreach (Dictionary<string, object> area in (List<object>)s["layout"])
            {
                foreach (Dictionary<string, object> sec in (List<object>)area["sections"])
                {
                    var suppressed = (bool)sec["suppressed"];
                    sb.AppendLine("- **" + area["kind"] + "** `" + sec["name"] + "` (height " + sec["height"] + ")" + (suppressed ? " _suppressed_" : ""));
                    foreach (Dictionary<string, object> o in (List<object>)sec["objects"])
                    {
                        var what = o.ContainsKey("field") ? "field `" + o["field"] + "`"
                                 : o.ContainsKey("text") ? "text \"" + Convert.ToString(o["text"]).Replace("\r", " ").Replace("\n", " ") + "\""
                                 : o.ContainsKey("subreport") ? "subreport `" + o["subreport"] + "`"
                                 : Convert.ToString(o["kind"]);
                        sb.AppendLine("  - " + what + " @ left " + o["left"] + ", top " + o["top"] + ", width " + o["width"] + (o.ContainsKey("suppressed") ? " _suppressed_" : ""));
                    }
                }
            }
            sb.AppendLine();
        }

        private static void Code(StringBuilder sb, string text, string lang)
        {
            if (string.IsNullOrWhiteSpace(text)) { sb.AppendLine("_None._"); sb.AppendLine(); return; }
            sb.AppendLine("```" + lang);
            sb.AppendLine(text.Trim());
            sb.AppendLine("```");
            sb.AppendLine();
        }

        private static string Instructions()
        {
            return
@"1. Create a .NET class library (or add to the existing solution) named after the report. No CrystalDecisions references.
2. **Data access**: one query per report/subreport. Build the SQL from *Data sources* + *Joins* (respect the join type: Equal = INNER, LeftOuter = LEFT JOIN, ...). Translate *Record selection* into the WHERE clause (parameterised - never concatenate parameter values). Use SQL commands verbatim when present.
3. **Models**: one POCO per row shape using the C# types in the field tables. Nullable for value types if the column can be NULL.
4. **Parameters**: a `ReportParameters` class with one property per Crystal parameter.
5. **Formulas**: one C# method or computed property per formula, named after the formula (e.g. `@Full Name` -> `FullName`). Keep exact semantics: null handling, string padding, date math, rounding (Crystal rounds half away from zero). Formulas that reference Previous/Next/WhilePrintingRecords/shared variables must be implemented during the row iteration, not per-row in isolation.
6. **Grouping/sorting/summaries**: use LINQ `GroupBy`/`OrderBy` in the listed order; implement summaries per group and grand totals; apply *Group selection* after grouping.
7. **Subreports**: a separate query executed per main-report row/group, filtered by the link fields.
8. **Output**: produce a view model that mirrors the layout bands (report header, page header, group headers, details, group footers, report footer). Then render it (e.g. QuestPDF, HTML, RDLC, Excel) - ask the user which output they want if not specified.
9. Write unit tests for every formula with representative inputs, including NULLs and boundary dates.
10. Do not invent fields: every table, field, formula and link you need is listed below.
11. **Visual FoxPro sources**: tables whose connection is Visual FoxPro (.dbf/.dbc) must be read through `System.Data.Odbc` (Microsoft Visual FoxPro ODBC driver, **32-bit only** - build x86) or `System.Data.OleDb` with VFPOLEDB. Use `?` positional parameters, trim blank-padded character fields, treat empty dates as null, and join FoxPro and SQL Server tables in memory (LINQ) because they cannot be joined in one SQL statement.";
        }

        private static string CheatSheet()
        {
            return
@"| Crystal | C# |
|---|---|
| `{Table.Field}` | `row.Field` |
| `{@Formula}` | `Formula(row)` / computed property |
| `{?Param}` | `parameters.Param` |
| `IsNull(x)` | `x == null` |
| `ToText(x, 2)` / `ToText(d, ""MM/dd/yyyy"")` | `x.ToString(""N2"")` / `d.ToString(""MM/dd/yyyy"")` |
| `Trim/LTrim/RTrim/UpperCase/LowerCase` | `Trim()/TrimStart()/TrimEnd()/ToUpper()/ToLower()` |
| `Left(s,n)/Right(s,n)/Mid(s,i,n)` (1-based) | `s.Substring(0,n)` / `s.Substring(s.Length-n)` / `s.Substring(i-1,n)` |
| `InStr(s, t)` (1-based, 0 = not found) | `s.IndexOf(t) + 1` |
| `Date(y,m,d)`, `CurrentDate`, `DateAdd(""d"",n,d)`, `DateDiff(""d"",a,b)` | `new DateTime(y,m,d)`, `DateTime.Today`, `d.AddDays(n)`, `(b - a).Days` |
| `Sum({x},{grp})`, `Count`, `Average`, `Maximum`, `Minimum` | LINQ `Sum/Count/Average/Max/Min` over the group |
| `if .. then .. else ..`, `Select Case` | `?:` / `switch` |
| `x in [a, b]`, `x in a to b` | `new[]{a,b}.Contains(x)`, `x >= a && x <= b` |
| `&` / `+` on strings | string concatenation |
| `Previous({f})`, `Next({f})`, `OnFirstRecord`, `OnLastRecord` | index-based access while iterating rows |
| `WhilePrintingRecords; Shared/Global NumberVar` | state variables updated in the render loop |";
        }

        private static string CopilotPrompt(List<string> requirements)
        {
            var p = CopilotPromptBase();
            if (requirements.Count > 0)
                p += "\n\nMy specific requirements (follow these first):\n- " + string.Join("\n- ", requirements);
            return p;
        }

        private static string CopilotPromptBase()
        {
            return
@"Read REPORT_SPEC.md and report-spec.json in this folder. They describe a SAP Crystal Report
(data sources, joins, parameters, record/group selection, formulas, grouping, summaries,
subreports and layout). Follow the 'Instructions for the coding agent' section to convert it into
a plain C# implementation with no Crystal Reports dependency:
data-access query, row models, parameters class, one C# method per formula (same semantics),
grouping/summaries, subreport queries, and a band-structured output model plus renderer.
Add unit tests for every formula. Ask me which output format (PDF/HTML/Excel) and which database
library (Dapper/ADO.NET/EF Core) to use if it is not already obvious from this solution.";
        }

        private static string PrettyJson(string json)
        {
            var sb = new StringBuilder();
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\' || (i > 1 && json[i - 2] == '\\'))) inString = !inString;
                if (inString) { sb.Append(c); continue; }
                switch (c)
                {
                    case '{': case '[':
                        sb.Append(c).AppendLine().Append(' ', ++indent * 2); break;
                    case '}': case ']':
                        sb.AppendLine().Append(' ', --indent * 2).Append(c); break;
                    case ',':
                        sb.Append(c).AppendLine().Append(' ', indent * 2); break;
                    case ':':
                        sb.Append(": "); break;
                    default:
                        sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
