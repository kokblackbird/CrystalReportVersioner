using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CrystalDecisions.ReportAppServer.ClientDoc;
using CrystalDecisions.ReportAppServer.Controllers;
using CrystalDecisions.ReportAppServer.DataDefModel;
using CrystalDecisions.ReportAppServer.ReportDefModel;
using CrEngine = CrystalDecisions.CrystalReports.Engine;
using CrShared = CrystalDecisions.Shared;

namespace ironmanx_04_2.Ai
{
    /// <summary>
    /// Dedicated STA thread that owns all Crystal / RAS COM objects for a report session.
    /// </summary>
    internal sealed class StaWorker : IDisposable
    {
        private readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();
        private readonly Thread _thread;

        public StaWorker()
        {
            _thread = new Thread(() =>
            {
                foreach (var work in _queue.GetConsumingEnumerable()) work();
            });
            _thread.IsBackground = true;
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Name = "Crystal RAS worker";
            _thread.Start();
        }

        public Task<T> Invoke<T>(Func<T> func)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add(() =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// Report editing toolset exposed to the AI agent. Wraps the in-process RAS object model
    /// (ReportDocument.ReportClientDocument) so the agent can change formulas, selection, layout,
    /// groups/sorts, tables and subreports, plus live database schema tools.
    /// All positions and sizes are in twips (1440 per inch).
    /// </summary>
    public sealed class ReportAgentTools : IDisposable
    {
        private readonly StaWorker _worker = new StaWorker();
        private readonly AiSettings _settings;
        private readonly DatabaseSchemaTools _db;
        public ResourceLibrary Resources { get; private set; }
        private CrEngine.ReportDocument _doc;
        private ISCDReportClientDocument _rcd;
        private readonly List<string> _tempFiles = new List<string>();

        public string SourcePath { get; private set; }
        public List<string> ChangeLog { get; } = new List<string>();
        public bool IsDirty { get { return ChangeLog.Count > 0; } }

        private ReportAgentTools(AiSettings settings)
        {
            _settings = settings;
            _db = new DatabaseSchemaTools(settings);
            Resources = new ResourceLibrary(settings);
        }

        /// <summary>Replaces the reference documents for this session (e.g. after the user adds files in the agent window).</summary>
        public void SetResources(System.Collections.Generic.IEnumerable<string> paths)
        {
            var copy = new AiSettings();
            copy.SensitiveKeywords = _settings.SensitiveKeywords;
            copy.ResourceFiles = paths.ToList();
            Resources = new ResourceLibrary(copy);
        }

        public static async Task<ReportAgentTools> OpenAsync(string path, AiSettings settings)
        {
            var tools = await Task.Run(() => new ReportAgentTools(settings)).ConfigureAwait(false);
            try
            {
                await tools._worker.Invoke(() =>
                {
                    tools._doc = new CrEngine.ReportDocument();
                    tools._doc.Load(path);
                    tools._rcd = tools._doc.ReportClientDocument;
                    tools.SourcePath = path;
                    return true;
                }).ConfigureAwait(false);
                return tools;
            }
            catch
            {
                tools.Dispose();
                throw;
            }
        }

        public Task SaveAsAsync(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(SourcePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to overwrite the original report. Choose a different file name.");
            return _worker.Invoke(() =>
            {
                if (File.Exists(path)) File.Delete(path);
                _doc.SaveAs(path);
                return true;
            });
        }

        public void Dispose()
        {
            try
            {
                _worker.Invoke(() =>
                {
                    try { if (_doc != null) { _doc.Close(); _doc.Dispose(); } } catch { }
                    return true;
                }).Wait(TimeSpan.FromSeconds(15));
            }
            catch { }
            _worker.Dispose();
            foreach (var f in _tempFiles) { try { File.Delete(f); } catch { } }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Tool definitions
        // ══════════════════════════════════════════════════════════════════════

        private static readonly Dictionary<string, object> SubreportArg =
            OpenAiClient.Str("Optional subreport name. Omit or empty to target the main report.");

        public IList<object> GetToolDefinitions()
        {
            var T = new List<object>();
            Func<string, string, Dictionary<string, object>, string[], Dictionary<string, object>> tool =
                (n, d, p, r) => OpenAiClient.Tool(n, d, p, r);

            // ── Inspection ──
            T.Add(tool("get_report_overview",
                "Returns the report structure: formulas, record/group selection, parameters, groups, sorts, tables, table links, areas/sections with every object (name, kind, position, text/data source), and subreport names. Call this first.",
                OpenAiClient.Props("subreport", SubreportArg), null));
            T.Add(tool("list_report_tables",
                "Lists the database tables/commands used by the report with their aliases, qualified names, connection kind, all fields (formula form like {Alias.Field} and type) and table links.",
                OpenAiClient.Props("subreport", SubreportArg), null));

            // ── Formulas & selection ──
            T.Add(tool("check_formula",
                "Syntax-checks formula text without changing the report. Returns ok or the Crystal error message.",
                OpenAiClient.Props("text", OpenAiClient.Str("Formula text"),
                                   "syntax", OpenAiClient.Str("Formula syntax", "crystal", "basic"),
                                   "subreport", SubreportArg), new[] { "text" }));
            T.Add(tool("set_formula",
                "Creates a formula field or replaces the text of an existing one (name without the @). The formula is syntax-checked before being applied.",
                OpenAiClient.Props("name", OpenAiClient.Str("Formula name without @"),
                                   "text", OpenAiClient.Str("Formula text"),
                                   "syntax", OpenAiClient.Str("Formula syntax (default crystal)", "crystal", "basic"),
                                   "subreport", SubreportArg), new[] { "name", "text" }));
            T.Add(tool("delete_formula", "Deletes a formula field (it must not be in use on the report).",
                OpenAiClient.Props("name", OpenAiClient.Str("Formula name without @"), "subreport", SubreportArg), new[] { "name" }));
            T.Add(tool("set_record_selection", "Replaces the record selection formula (use empty text to clear).",
                OpenAiClient.Props("text", OpenAiClient.Str("Boolean Crystal formula"), "subreport", SubreportArg), new[] { "text" }));
            T.Add(tool("set_group_selection", "Replaces the group selection formula (use empty text to clear).",
                OpenAiClient.Props("text", OpenAiClient.Str("Boolean Crystal formula"), "subreport", SubreportArg), new[] { "text" }));

            // ── Layout: objects ──
            var geometry = new object[]
            {
                "left", OpenAiClient.Int("Left in twips"), "top", OpenAiClient.Int("Top in twips"),
                "width", OpenAiClient.Int("Width in twips"), "height", OpenAiClient.Int("Height in twips")
            };
            var fontProps = new object[]
            {
                "font_name", OpenAiClient.Str("Font name (default Arial)"),
                "font_size", OpenAiClient.Num("Font size in points (default 10)"),
                "bold", OpenAiClient.Bool("Bold"), "italic", OpenAiClient.Bool("Italic"),
                "color", OpenAiClient.Str("Font color as #RRGGBB"),
                "alignment", OpenAiClient.Str("Horizontal alignment", "left", "center", "right", "justified", "default")
            };
            T.Add(tool("add_text_object",
                "Adds a text object (label) to a section. Use \\n for multiple lines.",
                OpenAiClient.Props(Concat(new object[]
                {
                    "section", OpenAiClient.Str("Section name, e.g. PageHeaderSection1, DetailSection1, GroupHeaderSection1"),
                    "text", OpenAiClient.Str("Text to display"),
                    "name", OpenAiClient.Str("Optional object name"),
                    "subreport", SubreportArg
                }, geometry, fontProps)), new[] { "section", "text", "left", "top", "width", "height" }));
            T.Add(tool("add_field_object",
                "Adds a field object bound to a database field {Alias.Field}, formula {@Name}, parameter {?Name}, running total {#Name} or special field to a section.",
                OpenAiClient.Props(Concat(new object[]
                {
                    "section", OpenAiClient.Str("Section name"),
                    "field", OpenAiClient.Str("Field in formula form, e.g. {Orders.Order Amount} or {@Total}"),
                    "name", OpenAiClient.Str("Optional object name"),
                    "subreport", SubreportArg
                }, geometry, fontProps)), new[] { "section", "field", "left", "top", "width", "height" }));
            T.Add(tool("add_line", "Adds a horizontal or vertical line in a section.",
                OpenAiClient.Props("section", OpenAiClient.Str("Section name"),
                    "left", OpenAiClient.Int("Start X twips"), "top", OpenAiClient.Int("Start Y twips"),
                    "right", OpenAiClient.Int("End X twips"), "bottom", OpenAiClient.Int("End Y twips"),
                    "thickness", OpenAiClient.Int("Line thickness in twips (default 20)"),
                    "style", OpenAiClient.Str("Line style", "single", "double", "dashed", "dotted"),
                    "subreport", SubreportArg), new[] { "section", "left", "top", "right", "bottom" }));
            T.Add(tool("add_box", "Adds a rectangle (box) in a section.",
                OpenAiClient.Props(Concat(new object[]
                {
                    "section", OpenAiClient.Str("Section name"),
                    "thickness", OpenAiClient.Int("Border thickness in twips (default 20)"),
                    "fill_color", OpenAiClient.Str("Optional fill color #RRGGBB"),
                    "subreport", SubreportArg
                }, geometry)), new[] { "section", "left", "top", "width", "height" }));
            T.Add(tool("update_object",
                "Modifies an existing report object by name: position/size, text (text objects), font, alignment, suppress, can-grow, conditional suppress formula.",
                OpenAiClient.Props(Concat(new object[]
                {
                    "name", OpenAiClient.Str("Object name from get_report_overview"),
                    "text", OpenAiClient.Str("New text (text objects only)"),
                    "suppress", OpenAiClient.Bool("Suppress the object"),
                    "can_grow", OpenAiClient.Bool("Can grow"),
                    "suppress_formula", OpenAiClient.Str("Conditional suppress formula (Crystal syntax); empty string clears"),
                    "subreport", SubreportArg
                }, geometry, fontProps)), new[] { "name" }));
            T.Add(tool("delete_object", "Removes a report object (field, text, line, box, subreport, chart...) by name.",
                OpenAiClient.Props("name", OpenAiClient.Str("Object name"), "subreport", SubreportArg), new[] { "name" }));

            // ── Layout: sections ──
            T.Add(tool("set_section_properties",
                "Changes section height and formatting: suppress, suppress if blank, new page before/after, keep together, background color, conditional suppress formula.",
                OpenAiClient.Props("section", OpenAiClient.Str("Section name"),
                    "height", OpenAiClient.Int("Height in twips"),
                    "suppress", OpenAiClient.Bool("Suppress (hide) the section"),
                    "suppress_if_blank", OpenAiClient.Bool("Suppress if blank"),
                    "new_page_before", OpenAiClient.Bool("New page before"),
                    "new_page_after", OpenAiClient.Bool("New page after"),
                    "keep_together", OpenAiClient.Bool("Keep together"),
                    "background_color", OpenAiClient.Str("Background color #RRGGBB"),
                    "suppress_formula", OpenAiClient.Str("Conditional suppress formula; empty string clears"),
                    "subreport", SubreportArg), new[] { "section" }));
            T.Add(tool("add_section",
                "Adds a new section to an area (e.g. a second Details section). Area names come from get_report_overview (e.g. DetailArea1, PageHeaderArea1, GroupHeaderArea1).",
                OpenAiClient.Props("area", OpenAiClient.Str("Area name"),
                    "height", OpenAiClient.Int("Height in twips (default 300)"),
                    "index", OpenAiClient.Int("Position within the area (default: end)"),
                    "subreport", SubreportArg), new[] { "area" }));
            T.Add(tool("remove_section", "Removes a section (the area must keep at least one section).",
                OpenAiClient.Props("section", OpenAiClient.Str("Section name"), "subreport", SubreportArg), new[] { "section" }));

            // ── Grouping / sorting ──
            T.Add(tool("add_group",
                "Adds a group on a field (creates group header/footer areas).",
                OpenAiClient.Props("field", OpenAiClient.Str("Field in formula form, e.g. {Customer.Region}"),
                    "date_condition", OpenAiClient.Str("For date fields", "daily", "weekly", "biweekly", "semimonthly", "monthly", "quarterly", "semiannually", "annually"),
                    "index", OpenAiClient.Int("Group level index (default: innermost)"),
                    "subreport", SubreportArg), new[] { "field" }));
            T.Add(tool("remove_group", "Removes a group by its zero-based index (see get_report_overview).",
                OpenAiClient.Props("index", OpenAiClient.Int("Group index"), "subreport", SubreportArg), new[] { "index" }));
            T.Add(tool("add_sort", "Adds a record sort on a field.",
                OpenAiClient.Props("field", OpenAiClient.Str("Field in formula form"),
                    "direction", OpenAiClient.Str("Sort direction", "ascending", "descending"),
                    "subreport", SubreportArg), new[] { "field" }));
            T.Add(tool("remove_sort", "Removes a record sort on a field.",
                OpenAiClient.Props("field", OpenAiClient.Str("Field in formula form"), "subreport", SubreportArg), new[] { "field" }));

            // ── Report data source ──
            T.Add(tool("add_table",
                "Adds a database table or view to the report (re-using the report's existing connection) and optionally links it to an existing table.",
                OpenAiClient.Props("table", OpenAiClient.Str("Table name, optionally qualified (e.g. dbo.Customers)"),
                    "alias", OpenAiClient.Str("Alias (default: table name)"),
                    "link_from_alias", OpenAiClient.Str("Existing table alias to link from"),
                    "link_from_fields", OpenAiClient.StrArray("Field names in the existing table"),
                    "link_to_fields", OpenAiClient.StrArray("Matching field names in the new table"),
                    "join_type", OpenAiClient.Str("Join type", "equal", "left_outer", "right_outer", "full_outer"),
                    "subreport", SubreportArg), new[] { "table" }));
            T.Add(tool("add_table_link", "Links two tables already in the report.",
                OpenAiClient.Props("from_alias", OpenAiClient.Str("Source table alias"),
                    "from_fields", OpenAiClient.StrArray("Source field names"),
                    "to_alias", OpenAiClient.Str("Target table alias"),
                    "to_fields", OpenAiClient.StrArray("Target field names"),
                    "join_type", OpenAiClient.Str("Join type", "equal", "left_outer", "right_outer", "full_outer"),
                    "subreport", SubreportArg), new[] { "from_alias", "from_fields", "to_alias", "to_fields" }));
            T.Add(tool("add_sql_command", "Adds a SQL Command (custom SQL) as a table using the report's existing connection.",
                OpenAiClient.Props("name", OpenAiClient.Str("Command name / alias"),
                    "sql", OpenAiClient.Str("SELECT statement"),
                    "subreport", SubreportArg), new[] { "name", "sql" }));
            T.Add(tool("remove_table", "Removes a table from the report by alias.",
                OpenAiClient.Props("alias", OpenAiClient.Str("Table alias"), "subreport", SubreportArg), new[] { "alias" }));

            // ── Subreports ──
            T.Add(tool("create_subreport",
                "Creates a new subreport object in a main-report section. By default creates a blank subreport using the same database connection (optionally keeping only the listed tables); or imports an existing .rpt via template_path. Then populate it with the other tools by passing subreport=<name>.",
                OpenAiClient.Props("name", OpenAiClient.Str("Subreport name"),
                    "section", OpenAiClient.Str("Main report section name to place it in"),
                    "left", OpenAiClient.Int("Left twips"), "top", OpenAiClient.Int("Top twips"),
                    "width", OpenAiClient.Int("Width twips"), "height", OpenAiClient.Int("Height twips"),
                    "keep_tables", OpenAiClient.StrArray("Aliases of main-report tables to keep in the blank subreport (default: all)"),
                    "template_path", OpenAiClient.Str("Optional full path of an existing .rpt to import as the subreport")),
                new[] { "name", "section", "left", "top", "width", "height" }));
            T.Add(tool("set_subreport_links",
                "Links a subreport to the main report (replaces existing links). Each link maps a main report field to a subreport field.",
                OpenAiClient.Props("subreport", OpenAiClient.Str("Subreport name"),
                    "main_fields", OpenAiClient.StrArray("Main report fields in formula form, e.g. {Customer.Customer ID}"),
                    "subreport_fields", OpenAiClient.StrArray("Matching subreport fields in formula form")),
                new[] { "subreport", "main_fields", "subreport_fields" }));

            // ── Validation ──
            T.Add(tool("validate_report",
                "Checks every formula and selection formula in the main report and all subreports. If enabled in settings, also test-renders the report against the database. Call before finishing.",
                OpenAiClient.Props(), null));
            T.Add(tool("render_report_pdf",
                "Renders the CURRENT (edited) report to PDF with the Crystal runtime so you can review the design. Returns the PDF path (opened for the user), page count, automatic layout checks (overlaps, objects outside section width, possible truncation) and, when the data access mode allows row data, the page-formatted text of the first pages. Call after significant layout changes and fix any problems it reveals.",
                OpenAiClient.Props("parameters", new Dictionary<string, object> { { "type", "object" }, { "description", "Optional report parameter values: {\"ParamName\": \"value\"}" }, { "additionalProperties", new Dictionary<string, object> { { "type", "string" } } } },
                                   "max_pages", OpenAiClient.Int("Pages of text to return (default 2)")), null));

            // ── Reference documents ──
            if (Resources.Documents.Count > 0)
            {
                T.Add(tool("list_resources", "Lists the reference documents the user attached (specs, business rules, SQL, style guides).", OpenAiClient.Props(), null));
                T.Add(tool("search_resources", "Keyword search across the user's reference documents. Returns matching snippets with offsets.",
                    OpenAiClient.Props("query", OpenAiClient.Str("Words to search for"), "max_hits", OpenAiClient.Int("Max hits (default 20)")), new[] { "query" }));
                T.Add(tool("read_resource", "Reads a reference document in 8000-character chunks. Use nextOffset to continue.",
                    OpenAiClient.Props("name", OpenAiClient.Str("Document name from list_resources"), "offset", OpenAiClient.Int("Character offset (default 0)")), new[] { "name" }));
            }

            // ── Live database ──
            T.Add(tool("db_list_tables",
                "Lists tables and views in the live database (configured in AI Settings). Optional name filter.",
                OpenAiClient.Props("filter", OpenAiClient.Str("Case-insensitive substring filter"),
                                   "max", OpenAiClient.Int("Max results (default 300)")), null));
            T.Add(tool("db_describe_table",
                "Describes a live database table/view: columns, types, nullability, keys and foreign keys. Columns matching the user's sensitive keywords are hidden.",
                OpenAiClient.Props("table", OpenAiClient.Str("Table name, optionally schema-qualified")), new[] { "table" }));
            if (_db.SamplingAllowed)
                T.Add(tool("db_sample_table",
                    "Returns a small RANDOM sample of rows from a table so you can see typical values. Sensitive columns/values are redacted.",
                    OpenAiClient.Props("table", OpenAiClient.Str("Table name"),
                                       "max_rows", OpenAiClient.Int("Max rows")), new[] { "table" }));
            if (_db.QueriesAllowed)
                T.Add(tool("db_query",
                    "Runs a read-only SELECT against the live database and returns a small sample of rows. Sensitive columns/values are redacted; queries mentioning sensitive keywords are blocked.",
                    OpenAiClient.Props("sql", OpenAiClient.Str("SELECT statement"),
                                       "max_rows", OpenAiClient.Int("Max rows")), new[] { "sql" }));
            return T;
        }

        private static object[] Concat(params object[][] parts)
        {
            return parts.SelectMany(p => p).ToArray();
        }

        // ══════════════════════════════════════════════════════════════════════
        // Dispatch
        // ══════════════════════════════════════════════════════════════════════

        public async Task<string> ExecuteAsync(string name, string argumentsJson)
        {
            Dictionary<string, object> args;
            try
            {
                args = string.IsNullOrWhiteSpace(argumentsJson)
                    ? new Dictionary<string, object>()
                    : (OpenAiClient.CreateSerializer().DeserializeObject(argumentsJson) as Dictionary<string, object>) ?? new Dictionary<string, object>();
            }
            catch (Exception ex)
            {
                return Json(Error("Invalid JSON arguments: " + ex.Message));
            }

            try
            {
                object result;
                if (name == "list_resources") result = Resources.List();
                else if (name == "search_resources") result = Resources.Search(Req(args, "query"), I(args, "max_hits", 20));
                else if (name == "read_resource") result = Resources.Read(Req(args, "name"), I(args, "offset", 0));
                else if (name.StartsWith("db_", StringComparison.Ordinal))
                {
                    result = await Task.Run(() => ExecuteDb(name, args)).ConfigureAwait(false);
                }
                else
                {
                    result = await _worker.Invoke(() => ExecuteReport(name, args)).ConfigureAwait(false);
                }
                return Json(result);
            }
            catch (Exception ex)
            {
                var inner = ex is AggregateException && ex.InnerException != null ? ex.InnerException : ex;
                return Json(Error(inner.GetType().Name + ": " + inner.Message));
            }
        }

        private object ExecuteDb(string name, Dictionary<string, object> a)
        {
            if (!_db.IsConfigured) return Error("No live database connection is configured in AI Settings. Use list_report_tables for the report's own schema.");
            switch (name)
            {
                case "db_list_tables": return _db.ListTables(S(a, "filter"), I(a, "max", 300));
                case "db_describe_table": return _db.DescribeTable(Req(a, "table"));
                case "db_sample_table": return _db.SampleTable(Req(a, "table"), I(a, "max_rows", _settings.SqlQueryMaxRows));
                case "db_query": return _db.RunQuery(Req(a, "sql"), I(a, "max_rows", _settings.SqlQueryMaxRows));
                default: return Error("Unknown tool: " + name);
            }
        }

        private object ExecuteReport(string name, Dictionary<string, object> a)
        {
            switch (name)
            {
                case "get_report_overview": return GetOverview(Scope(a));
                case "list_report_tables": return ListTables(Scope(a));
                case "check_formula": return CheckFormula(Scope(a), Req(a, "text"), S(a, "syntax"));
                case "set_formula": return SetFormula(Scope(a), Req(a, "name"), ReqAllowEmpty(a, "text"), S(a, "syntax"));
                case "delete_formula": return DeleteFormula(Scope(a), Req(a, "name"));
                case "set_record_selection": return SetSelection(Scope(a), ReqAllowEmpty(a, "text"), false);
                case "set_group_selection": return SetSelection(Scope(a), ReqAllowEmpty(a, "text"), true);
                case "add_text_object": return AddText(Scope(a), a);
                case "add_field_object": return AddField(Scope(a), a);
                case "add_line": return AddLine(Scope(a), a);
                case "add_box": return AddBox(Scope(a), a);
                case "update_object": return UpdateObject(Scope(a), a);
                case "delete_object": return DeleteObject(Scope(a), Req(a, "name"));
                case "set_section_properties": return SetSectionProperties(Scope(a), a);
                case "add_section": return AddSection(Scope(a), a);
                case "remove_section": return RemoveSection(Scope(a), Req(a, "section"));
                case "add_group": return AddGroup(Scope(a), a);
                case "remove_group": return RemoveGroup(Scope(a), I(a, "index", -1));
                case "add_sort": return AddSort(Scope(a), Req(a, "field"), S(a, "direction"));
                case "remove_sort": return RemoveSort(Scope(a), Req(a, "field"));
                case "add_table": return AddTable(Scope(a), a);
                case "add_table_link": return AddTableLink(Scope(a), a);
                case "add_sql_command": return AddSqlCommand(Scope(a), Req(a, "name"), Req(a, "sql"));
                case "remove_table": return RemoveTable(Scope(a), Req(a, "alias"));
                case "create_subreport": return CreateSubreport(a);
                case "set_subreport_links": return SetSubreportLinks(a);
                case "validate_report": return ValidateReport();
                case "render_report_pdf": return RenderReportPdf(a);
                default: return Error("Unknown tool: " + name);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Scope (main report or subreport)
        // ══════════════════════════════════════════════════════════════════════

        private sealed class ReportScope
        {
            public string Name;
            public DataDefController DataDef;
            public DatabaseController Database;
            public ReportDefController2 ReportDef;
            public string Label { get { return string.IsNullOrEmpty(Name) ? "main report" : "subreport '" + Name + "'"; } }
        }

        private ReportScope Scope(Dictionary<string, object> a)
        {
            return Scope(S(a, "subreport"));
        }

        private ReportScope Scope(string subreport)
        {
            if (string.IsNullOrWhiteSpace(subreport))
            {
                return new ReportScope
                {
                    Name = null,
                    DataDef = _rcd.DataDefController,
                    Database = _rcd.DatabaseController,
                    ReportDef = _rcd.ReportDefController
                };
            }
            var names = SubreportNames();
            var match = names.FirstOrDefault(n => string.Equals(n, subreport, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new ArgumentException("Subreport not found: " + subreport + ". Available: " + string.Join(", ", names));
            var sub = _rcd.SubreportController.GetSubreport(match);
            return new ReportScope
            {
                Name = match,
                DataDef = sub.DataDefController,
                Database = sub.DatabaseController,
                ReportDef = sub.ReportDefController
            };
        }

        private List<string> SubreportNames()
        {
            var list = new List<string>();
            var names = _rcd.SubreportController.GetSubreportNames();
            if (names != null) foreach (string n in names) list.Add(n);
            return list;
        }

        private void Changed(ReportScope scope, string description)
        {
            ChangeLog.Add("[" + scope.Label + "] " + description);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Inspection
        // ══════════════════════════════════════════════════════════════════════

        private object GetOverview(ReportScope s)
        {
            var dd = s.DataDef.DataDefinition;
            var result = new Dictionary<string, object>();
            result["scope"] = s.Label;
            result["units"] = "twips (1440 per inch)";
            result["recordSelection"] = SafeGet(() => s.DataDef.RecordFilterController.GetFormulaText());
            result["groupSelection"] = SafeGet(() => s.DataDef.GroupFilterController.GetFormulaText());

            var formulas = new List<object>();
            foreach (ISCRField f in dd.FormulaFields)
            {
                var ff = f as ISCRFormulaField;
                formulas.Add(new Dictionary<string, object>
                {
                    { "name", f.Name }, { "formulaForm", f.FormulaForm },
                    { "text", ff != null ? ff.Text : null },
                    { "syntax", ff != null ? ff.Syntax.ToString() : null }
                });
            }
            result["formulas"] = formulas;

            var parameters = new List<object>();
            foreach (ISCRField p in dd.ParameterFields)
                parameters.Add(new Dictionary<string, object> { { "name", p.Name }, { "formulaForm", p.FormulaForm }, { "type", p.Type.ToString() } });
            result["parameters"] = parameters;

            var groups = new List<object>();
            int gi = 0;
            foreach (Group g in dd.Groups)
                groups.Add(new Dictionary<string, object> { { "index", gi++ }, { "field", g.ConditionField != null ? g.ConditionField.FormulaForm : null } });
            result["groups"] = groups;

            var sorts = new List<object>();
            foreach (ISCRSort so in dd.Sorts)
                sorts.Add(new Dictionary<string, object> { { "field", so.SortField != null ? so.SortField.FormulaForm : null }, { "direction", so.Direction.ToString() } });
            result["sorts"] = sorts;

            var summaries = new List<object>();
            foreach (ISCRField sf in dd.SummaryFields) summaries.Add(sf.FormulaForm);
            result["summaryFields"] = summaries;

            var rts = new List<object>();
            foreach (ISCRField rt in dd.RunningTotalFields) rts.Add(rt.FormulaForm);
            result["runningTotals"] = rts;

            var tables = new List<object>();
            foreach (ISCRTable t in s.Database.Database.Tables)
                tables.Add(new Dictionary<string, object> { { "alias", t.Alias }, { "name", t.Name }, { "qualifiedName", t.QualifiedName } });
            result["tables"] = tables;
            result["tableLinks"] = DescribeLinks(s);

            var areas = new List<object>();
            var rd = s.ReportDef.ReportDefinition;
            foreach (ISCRArea area in rd.Areas)
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
                areas.Add(new Dictionary<string, object> { { "name", area.Name }, { "kind", area.Kind.ToString().Replace("crAreaSectionKind", "") }, { "sections", sections } });
            }
            result["areas"] = areas;
            if (string.IsNullOrEmpty(s.Name)) result["subreports"] = SubreportNames();
            return result;
        }

        private static Dictionary<string, object> DescribeObject(ISCRReportObject o)
        {
            var d = new Dictionary<string, object>
            {
                { "name", o.Name },
                { "kind", o.Kind.ToString().Replace("crReportObjectKind", "") },
                { "left", o.Left }, { "top", o.Top }, { "width", o.Width }, { "height", o.Height }
            };
            var text = o as ISCRTextObject;
            if (text != null) d["text"] = SafeGet(() => text.Text);
            var field = o as ISCRFieldObject;
            if (field != null) d["field"] = field.DataSourceName;
            var sub = o as ISCRSubreportObject;
            if (sub != null) d["subreport"] = sub.SubreportName;
            if (o.Format != null && o.Format.EnableSuppress) d["suppressed"] = true;
            return d;
        }

        private object ListTables(ReportScope s)
        {
            var tables = new List<object>();
            foreach (ISCRTable t in s.Database.Database.Tables)
            {
                var fields = new List<object>();
                int count = 0;
                if (t.DataFields != null)
                {
                    foreach (ISCRField f in t.DataFields)
                    {
                        if (count++ >= 250) break;
                        fields.Add(new Dictionary<string, object> { { "field", f.FormulaForm }, { "type", f.Type.ToString().Replace("crFieldValueType", "").Replace("Field", "") } });
                    }
                }
                tables.Add(new Dictionary<string, object>
                {
                    { "alias", t.Alias }, { "name", t.Name }, { "qualifiedName", t.QualifiedName },
                    { "connectionKind", t.ConnectionInfo != null ? t.ConnectionInfo.Kind.ToString() : null },
                    { "fields", fields }
                });
            }
            return new Dictionary<string, object> { { "scope", s.Label }, { "tables", tables }, { "links", DescribeLinks(s) } };
        }

        private static List<object> DescribeLinks(ReportScope s)
        {
            var links = new List<object>();
            var tl = s.Database.Database.TableLinks;
            if (tl == null) return links;
            foreach (TableLink l in tl)
            {
                links.Add(new Dictionary<string, object>
                {
                    { "from", l.SourceTableAlias }, { "fromFields", StringsToList(l.SourceFieldNames) },
                    { "to", l.TargetTableAlias }, { "toFields", StringsToList(l.TargetFieldNames) },
                    { "join", l.JoinType.ToString().Replace("crTableJoinType", "") }
                });
            }
            return links;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Formulas & selection
        // ══════════════════════════════════════════════════════════════════════

        private static CrFormulaSyntaxEnum ParseSyntax(string syntax)
        {
            return string.Equals(syntax, "basic", StringComparison.OrdinalIgnoreCase)
                ? CrFormulaSyntaxEnum.crFormulaSyntaxBasic
                : CrFormulaSyntaxEnum.crFormulaSyntaxCrystal;
        }

        private static string RunCheck(ReportScope s, string name, string text, CrFormulaSyntaxEnum syntax)
        {
            var probe = new FormulaFieldClass { Name = name, Text = text, Syntax = syntax };
            try
            {
                var msg = s.DataDef.FormulaFieldController.Check(probe);
                return string.IsNullOrWhiteSpace(msg) ? null : msg;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private object CheckFormula(ReportScope s, string text, string syntax)
        {
            var err = RunCheck(s, "__ai_check", text, ParseSyntax(syntax));
            return err == null ? Ok("Formula is valid.") : Error("Formula error: " + err);
        }

        private ISCRFormulaField FindFormula(ReportScope s, string name)
        {
            name = (name ?? string.Empty).TrimStart('@').Trim('{', '}').TrimStart('@');
            foreach (ISCRField f in s.DataDef.DataDefinition.FormulaFields)
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f as ISCRFormulaField;
            return null;
        }

        private object SetFormula(ReportScope s, string name, string text, string syntax)
        {
            name = name.Trim().TrimStart('{').TrimEnd('}').TrimStart('@');
            var syn = ParseSyntax(syntax);
            var err = RunCheck(s, name, text, syn);
            if (err != null) return Error("Formula not applied. Crystal reports: " + err);

            var existing = FindFormula(s, name);
            if (existing == null)
            {
                var ff = new FormulaFieldClass { Name = name, Text = text, Syntax = syn };
                s.DataDef.FormulaFieldController.Add(ff);
                Changed(s, "Added formula @" + name);
                return Ok("Formula {@" + name + "} created.");
            }
            var updated = (FormulaField)existing.Clone(true);
            updated.Text = text;
            updated.Syntax = syn;
            s.DataDef.FormulaFieldController.Modify(existing, updated);
            Changed(s, "Modified formula @" + name);
            return Ok("Formula {@" + name + "} updated.");
        }

        private object DeleteFormula(ReportScope s, string name)
        {
            var existing = FindFormula(s, name);
            if (existing == null) return Error("Formula not found: " + name);
            s.DataDef.FormulaFieldController.Remove(existing);
            Changed(s, "Deleted formula @" + existing.Name);
            return Ok("Formula deleted.");
        }

        private object SetSelection(ReportScope s, string text, bool group)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                var err = RunCheck(s, "__ai_selection", text, CrFormulaSyntaxEnum.crFormulaSyntaxCrystal);
                if (err != null) return Error("Selection formula not applied. Crystal reports: " + err);
            }
            var ctrl = group ? s.DataDef.GroupFilterController : s.DataDef.RecordFilterController;
            ctrl.SetFormulaText(text ?? string.Empty);
            Changed(s, (group ? "Group" : "Record") + " selection set to: " + Truncate(text, 200));
            return Ok((group ? "Group" : "Record") + " selection updated.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Layout: objects
        // ══════════════════════════════════════════════════════════════════════

        private static Section FindSection(ReportScope s, string name)
        {
            var sec = s.ReportDef.ReportDefinition.FindSectionByName(name);
            if (sec == null) throw new ArgumentException("Section not found: " + name + ". Use get_report_overview for section names.");
            return sec;
        }

        private static ISCRReportObject FindObject(ReportScope s, string name)
        {
            var obj = s.ReportDef.ReportDefinition.FindObjectByName(name);
            if (obj == null) throw new ArgumentException("Object not found: " + name + ". Use get_report_overview for object names.");
            return obj;
        }

        private static FontColor MakeFontColor(Dictionary<string, object> a, FontColor baseline)
        {
            FontColor fc = baseline != null ? baseline.Clone(true) : new FontColorClass();
            Font font = fc.Font != null ? fc.Font : new FontClass { Name = "Arial", Size = 10m };
            if (font.Name == null) font.Name = "Arial";
            var fontName = S(a, "font_name");
            if (!string.IsNullOrWhiteSpace(fontName)) font.Name = fontName;
            if (a.ContainsKey("font_size")) font.Size = Convert.ToDecimal(a["font_size"], CultureInfo.InvariantCulture);
            else if (font.Size <= 0) font.Size = 10m;
            if (a.ContainsKey("bold")) font.Bold = B(a, "bold");
            if (a.ContainsKey("italic")) font.Italic = B(a, "italic");
            fc.Font = font;
            var color = S(a, "color");
            if (!string.IsNullOrWhiteSpace(color)) fc.Color = ParseColor(color);
            return fc;
        }

        private static bool HasFontArgs(Dictionary<string, object> a)
        {
            return a.ContainsKey("font_name") || a.ContainsKey("font_size") || a.ContainsKey("bold") || a.ContainsKey("italic") || a.ContainsKey("color");
        }

        private static CrAlignmentEnum ParseAlignment(string value)
        {
            switch ((value ?? string.Empty).ToLowerInvariant())
            {
                case "left": return CrAlignmentEnum.crAlignmentLeft;
                case "center": return CrAlignmentEnum.crAlignmentHorizontalCenter;
                case "right": return CrAlignmentEnum.crAlignmentRight;
                case "justified": return CrAlignmentEnum.crAlignmentJustified;
                default: return CrAlignmentEnum.crAlignmentDefault;
            }
        }

        private static uint ParseColor(string hex)
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6) throw new ArgumentException("Color must be #RRGGBB");
            var r = Convert.ToUInt32(hex.Substring(0, 2), 16);
            var g = Convert.ToUInt32(hex.Substring(2, 2), 16);
            var b = Convert.ToUInt32(hex.Substring(4, 2), 16);
            return r | (g << 8) | (b << 16); // Crystal stores colors as BGR (COLORREF)
        }

        private static Paragraphs BuildParagraphs(string text, FontColor fc, CrAlignmentEnum alignment)
        {
            var paragraphs = new ParagraphsClass();
            var lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            foreach (var line in lines)
            {
                var p = new ParagraphClass { Alignment = alignment, FontColor = fc.Clone(true) };
                var elements = new ParagraphElementsClass();
                var el = new ParagraphTextElementClass
                {
                    Kind = CrParagraphElementKindEnum.crParagraphElementKindText,
                    Text = line,
                    FontColor = fc.Clone(true)
                };
                elements.Add(el);
                p.ParagraphElements = elements;
                paragraphs.Add(p);
            }
            return paragraphs;
        }

        private static void ApplyGeometry(ISCRReportObject o, Dictionary<string, object> a)
        {
            if (a.ContainsKey("left")) o.Left = I(a, "left", o.Left);
            if (a.ContainsKey("top")) o.Top = I(a, "top", o.Top);
            if (a.ContainsKey("width")) o.Width = I(a, "width", o.Width);
            if (a.ContainsKey("height")) o.Height = I(a, "height", o.Height);
        }

        private object AddText(ReportScope s, Dictionary<string, object> a)
        {
            var section = FindSection(s, Req(a, "section"));
            var fc = MakeFontColor(a, null);
            var align = ParseAlignment(S(a, "alignment"));
            var text = new TextObjectClass();
            var name = S(a, "name");
            if (!string.IsNullOrWhiteSpace(name)) text.Name = name;
            ApplyGeometry(text, a);
            text.FontColor = fc;
            text.Paragraphs = BuildParagraphs(Req(a, "text"), fc, align);
            var fmt = new ObjectFormatClass { HorizontalAlignment = align, EnableCanGrow = false };
            text.Format = fmt;
            s.ReportDef.ReportObjectController.Add(text, section, -1);
            Changed(s, "Added text '" + Truncate(S(a, "text"), 60) + "' to " + section.Name);
            return Ok("Text object added to " + section.Name + ".");
        }

        private object AddField(ReportScope s, Dictionary<string, object> a)
        {
            var section = FindSection(s, Req(a, "section"));
            var formulaForm = NormalizeFieldRef(Req(a, "field"));
            var field = s.DataDef.FindFieldByFormulaForm(formulaForm);
            if (field == null) return Error("Field not found: " + formulaForm + ". Use list_report_tables / get_report_overview, or add_table first.");

            var fo = new FieldObjectClass();
            var name = S(a, "name");
            if (!string.IsNullOrWhiteSpace(name)) fo.Name = name;
            fo.FieldValueType = field.Type;
            fo.DataSourceName = field.FormulaForm;
            ApplyGeometry(fo, a);
            fo.FontColor = MakeFontColor(a, null);
            var fmt = new ObjectFormatClass { HorizontalAlignment = ParseAlignment(S(a, "alignment")) };
            fo.Format = fmt;
            s.ReportDef.ReportObjectController.Add(fo, section, -1);
            Changed(s, "Added field " + field.FormulaForm + " to " + section.Name);
            return Ok("Field object " + field.FormulaForm + " added to " + section.Name + ".");
        }

        private static CrLineStyleEnum ParseLineStyle(string v)
        {
            switch ((v ?? string.Empty).ToLowerInvariant())
            {
                case "double": return CrLineStyleEnum.crLineStyleDouble;
                case "dashed": return CrLineStyleEnum.crLineStyleDashed;
                case "dotted": return CrLineStyleEnum.crLineStyleDotted;
                default: return CrLineStyleEnum.crLineStyleSingle;
            }
        }

        private object AddLine(ReportScope s, Dictionary<string, object> a)
        {
            var section = FindSection(s, Req(a, "section"));
            var line = new LineObjectClass
            {
                Left = I(a, "left", 0),
                Top = I(a, "top", 0),
                Right = I(a, "right", 0),
                Bottom = I(a, "bottom", 0),
                LineThickness = I(a, "thickness", 20),
                LineStyle = ParseLineStyle(S(a, "style")),
                SectionName = section.Name,
                EndSectionName = section.Name
            };
            s.ReportDef.ReportObjectController.Add(line, section, -1);
            Changed(s, "Added line to " + section.Name);
            return Ok("Line added to " + section.Name + ".");
        }

        private object AddBox(ReportScope s, Dictionary<string, object> a)
        {
            var section = FindSection(s, Req(a, "section"));
            var left = I(a, "left", 0);
            var top = I(a, "top", 0);
            var box = new BoxObjectClass
            {
                Left = left,
                Top = top,
                Right = left + I(a, "width", 1440),
                Bottom = top + I(a, "height", 300),
                LineThickness = I(a, "thickness", 20),
                LineStyle = CrLineStyleEnum.crLineStyleSingle,
                SectionName = section.Name,
                EndSectionName = section.Name
            };
            var fill = S(a, "fill_color");
            if (!string.IsNullOrWhiteSpace(fill)) box.FillColor = ParseColor(fill);
            s.ReportDef.ReportObjectController.Add(box, section, -1);
            Changed(s, "Added box to " + section.Name);
            return Ok("Box added to " + section.Name + ".");
        }

        private object UpdateObject(ReportScope s, Dictionary<string, object> a)
        {
            var original = FindObject(s, Req(a, "name"));
            var clone = original.Clone(true);
            ApplyGeometry(clone, a);

            var text = clone as ISCRTextObject;
            var field = clone as ISCRFieldObject;
            if (text != null && (a.ContainsKey("text") || HasFontArgs(a) || a.ContainsKey("alignment")))
            {
                var fc = MakeFontColor(a, text.FontColor);
                text.FontColor = fc;
                var newText = a.ContainsKey("text") ? S(a, "text") : SafeGet(() => text.Text);
                var align = a.ContainsKey("alignment") ? ParseAlignment(S(a, "alignment")) :
                    (text.Paragraphs != null && text.Paragraphs.Count > 0 ? text.Paragraphs[0].Alignment : CrAlignmentEnum.crAlignmentDefault);
                text.Paragraphs = BuildParagraphs(newText, fc, align);
            }
            else if (a.ContainsKey("text"))
            {
                return Error("'text' can only be changed on text objects. This object is " + clone.Kind);
            }

            if (field != null && HasFontArgs(a)) field.FontColor = MakeFontColor(a, field.FontColor);

            if (a.ContainsKey("suppress") || a.ContainsKey("can_grow") || a.ContainsKey("alignment") || a.ContainsKey("suppress_formula"))
            {
                ObjectFormat fmt = clone.Format != null ? clone.Format.Clone(true) : new ObjectFormatClass();
                if (a.ContainsKey("suppress")) fmt.EnableSuppress = B(a, "suppress");
                if (a.ContainsKey("can_grow")) fmt.EnableCanGrow = B(a, "can_grow");
                if (a.ContainsKey("alignment")) fmt.HorizontalAlignment = ParseAlignment(S(a, "alignment"));
                if (a.ContainsKey("suppress_formula"))
                {
                    var f = S(a, "suppress_formula") ?? string.Empty;
                    if (f.Length > 0)
                    {
                        var err = RunCheck(s, "__ai_cond", f, CrFormulaSyntaxEnum.crFormulaSyntaxCrystal);
                        if (err != null) return Error("Suppress formula invalid: " + err);
                    }
                    fmt.ConditionFormulas[CrObjectFormatConditionFormulaTypeEnum.crObjectFormatConditionFormulaTypeEnableSuppress] =
                        new ConditionFormulaClass { Text = f, Syntax = CrFormulaSyntaxEnum.crFormulaSyntaxCrystal };
                }
                clone.Format = fmt;
            }

            s.ReportDef.ReportObjectController.Modify(original, clone);
            Changed(s, "Updated object " + original.Name);
            return Ok("Object " + original.Name + " updated.");
        }

        private object DeleteObject(ReportScope s, string name)
        {
            var obj = FindObject(s, name);
            s.ReportDef.ReportObjectController.Remove(obj);
            Changed(s, "Deleted object " + name);
            return Ok("Object " + name + " deleted.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Layout: sections
        // ══════════════════════════════════════════════════════════════════════

        private object SetSectionProperties(ReportScope s, Dictionary<string, object> a)
        {
            var name = Req(a, "section");
            var ctrl = s.ReportDef.ReportSectionController;
            var section = FindSection(s, name);
            var changes = new List<string>();

            bool formatChange = a.ContainsKey("suppress") || a.ContainsKey("suppress_if_blank") || a.ContainsKey("new_page_before") ||
                                a.ContainsKey("new_page_after") || a.ContainsKey("keep_together") || a.ContainsKey("background_color") ||
                                a.ContainsKey("suppress_formula");
            if (formatChange)
            {
                SectionFormat fmt = section.Format.Clone(true);
                if (a.ContainsKey("suppress")) { fmt.EnableSuppress = B(a, "suppress"); changes.Add("suppress"); }
                if (a.ContainsKey("suppress_if_blank")) { fmt.EnableSuppressIfBlank = B(a, "suppress_if_blank"); changes.Add("suppress_if_blank"); }
                if (a.ContainsKey("new_page_before")) { fmt.EnableNewPageBefore = B(a, "new_page_before"); changes.Add("new_page_before"); }
                if (a.ContainsKey("new_page_after")) { fmt.EnableNewPageAfter = B(a, "new_page_after"); changes.Add("new_page_after"); }
                if (a.ContainsKey("keep_together")) { fmt.EnableKeepTogether = B(a, "keep_together"); changes.Add("keep_together"); }
                if (a.ContainsKey("background_color")) { fmt.BackgroundColor = ParseColor(S(a, "background_color")); changes.Add("background_color"); }
                if (a.ContainsKey("suppress_formula"))
                {
                    var f = S(a, "suppress_formula") ?? string.Empty;
                    if (f.Length > 0)
                    {
                        var err = RunCheck(s, "__ai_cond", f, CrFormulaSyntaxEnum.crFormulaSyntaxCrystal);
                        if (err != null) return Error("Suppress formula invalid: " + err);
                    }
                    fmt.ConditionFormulas[CrSectionAreaFormatConditionFormulaTypeEnum.crSectionAreaConditionFormulaTypeEnableSuppress] =
                        new ConditionFormulaClass { Text = f, Syntax = CrFormulaSyntaxEnum.crFormulaSyntaxCrystal };
                    changes.Add("suppress_formula");
                }
                ctrl.SetProperty(section, CrReportSectionPropertyEnum.crReportSectionPropertyFormat, fmt);
            }
            if (a.ContainsKey("height"))
            {
                section = FindSection(s, name);
                ctrl.SetProperty(section, CrReportSectionPropertyEnum.crReportSectionPropertyHeight, I(a, "height", section.Height));
                changes.Add("height");
            }
            if (changes.Count == 0) return Error("No properties specified.");
            Changed(s, "Section " + name + ": " + string.Join(", ", changes));
            return Ok("Section " + name + " updated: " + string.Join(", ", changes));
        }

        private object AddSection(ReportScope s, Dictionary<string, object> a)
        {
            var areaName = Req(a, "area");
            ISCRArea area = null;
            foreach (ISCRArea ar in s.ReportDef.ReportDefinition.Areas)
                if (string.Equals(ar.Name, areaName, StringComparison.OrdinalIgnoreCase)) { area = ar; break; }
            if (area == null) return Error("Area not found: " + areaName);
            var section = new SectionClass { Kind = area.Kind, Height = I(a, "height", 300) };
            var index = I(a, "index", area.Sections.Count);
            s.ReportDef.ReportSectionController.Add(section, area, index);
            Changed(s, "Added section to " + area.Name);
            var refreshed = s.ReportDef.ReportDefinition;
            var names = new List<string>();
            foreach (ISCRArea ar in refreshed.Areas)
                if (string.Equals(ar.Name, area.Name, StringComparison.OrdinalIgnoreCase))
                    foreach (Section sec in ar.Sections) names.Add(sec.Name);
            return Ok("Section added. Sections now in " + area.Name + ": " + string.Join(", ", names));
        }

        private object RemoveSection(ReportScope s, string name)
        {
            var section = FindSection(s, name);
            s.ReportDef.ReportSectionController.Remove(section);
            Changed(s, "Removed section " + name);
            return Ok("Section " + name + " removed.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Grouping / sorting
        // ══════════════════════════════════════════════════════════════════════

        private ISCRField ResolveField(ReportScope s, string field)
        {
            var ff = NormalizeFieldRef(field);
            var f = s.DataDef.FindFieldByFormulaForm(ff);
            if (f == null) throw new ArgumentException("Field not found: " + ff);
            return f;
        }

        private object AddGroup(ReportScope s, Dictionary<string, object> a)
        {
            var field = ResolveField(s, Req(a, "field"));
            var dc = S(a, "date_condition");
            var index = I(a, "index", -1);
            if (!string.IsNullOrWhiteSpace(dc))
            {
                CrDateConditionEnum cond;
                if (!Enum.TryParse("crDateCondition" + dc, true, out cond)) return Error("Unknown date_condition: " + dc);
                s.DataDef.GroupController.AddByName(index, field.FormulaForm, cond);
            }
            else
            {
                if (!s.DataDef.GroupController.CanGroupOn(field)) return Error("Cannot group on " + field.FormulaForm);
                var g = new GroupClass { ConditionField = field };
                s.DataDef.GroupController.Add(index, g);
            }
            Changed(s, "Added group on " + field.FormulaForm);
            return Ok("Group added on " + field.FormulaForm + ". New group header/footer sections were created; call get_report_overview to see their names.");
        }

        private object RemoveGroup(ReportScope s, int index)
        {
            var groups = s.DataDef.DataDefinition.Groups;
            if (index < 0 || index >= groups.Count) return Error("Group index out of range. Count = " + groups.Count);
            var g = groups[index];
            var label = g.ConditionField != null ? g.ConditionField.FormulaForm : index.ToString();
            s.DataDef.GroupController.Remove(g);
            Changed(s, "Removed group " + label);
            return Ok("Group removed.");
        }

        private object AddSort(ReportScope s, string fieldRef, string direction)
        {
            var field = ResolveField(s, fieldRef);
            if (!s.DataDef.SortController.CanSortOn(field)) return Error("Cannot sort on " + field.FormulaForm);
            var sort = new SortClass
            {
                SortField = field,
                Direction = string.Equals(direction, "descending", StringComparison.OrdinalIgnoreCase)
                    ? CrSortDirectionEnum.crSortDirectionDescendingOrder
                    : CrSortDirectionEnum.crSortDirectionAscendingOrder
            };
            s.DataDef.SortController.Add(-1, sort);
            Changed(s, "Added sort on " + field.FormulaForm + " " + (direction ?? "ascending"));
            return Ok("Sort added.");
        }

        private object RemoveSort(ReportScope s, string fieldRef)
        {
            var ff = NormalizeFieldRef(fieldRef);
            foreach (ISCRSort so in s.DataDef.DataDefinition.Sorts)
            {
                if (so.SortField != null && string.Equals(so.SortField.FormulaForm, ff, StringComparison.OrdinalIgnoreCase))
                {
                    s.DataDef.SortController.Remove(so);
                    Changed(s, "Removed sort on " + ff);
                    return Ok("Sort removed.");
                }
            }
            return Error("No sort found on " + ff);
        }

        // ══════════════════════════════════════════════════════════════════════
        // Data source
        // ══════════════════════════════════════════════════════════════════════

        private static CrTableJoinTypeEnum ParseJoin(string v)
        {
            switch ((v ?? string.Empty).ToLowerInvariant())
            {
                case "left_outer": return CrTableJoinTypeEnum.crTableJoinTypeLeftOuterJoin;
                case "right_outer": return CrTableJoinTypeEnum.crTableJoinTypeRightOuterJoin;
                case "full_outer": return CrTableJoinTypeEnum.crTableJoinTypeOuterJoin;
                default: return CrTableJoinTypeEnum.crTableJoinTypeEqualJoin;
            }
        }

        private ISCRTable FirstTable(ReportScope s)
        {
            var tables = s.Database.Database.Tables;
            if (tables == null || tables.Count == 0)
            {
                // Fall back to the main report's connection (useful for blank subreports).
                var main = _rcd.DatabaseController.Database.Tables;
                if (main == null || main.Count == 0) throw new InvalidOperationException("The report has no existing table to copy a connection from.");
                return main[0];
            }
            return tables[0];
        }

        private object AddTable(ReportScope s, Dictionary<string, object> a)
        {
            var tableName = Req(a, "table").Trim();
            var template = FirstTable(s);
            var shortName = tableName.Contains(".") ? tableName.Substring(tableName.LastIndexOf('.') + 1) : tableName;
            var alias = S(a, "alias");
            if (string.IsNullOrWhiteSpace(alias)) alias = shortName;

            string qualified;
            var templateQualified = template.QualifiedName ?? string.Empty;
            var templateParts = templateQualified.Split('.');
            var requestParts = tableName.Split('.');
            if (templateParts.Length > requestParts.Length)
            {
                // Keep the catalog/owner prefix from the existing table, replacing the trailing parts.
                var prefix = templateParts.Take(templateParts.Length - requestParts.Length);
                qualified = string.Join(".", prefix.Concat(requestParts));
            }
            else
            {
                qualified = tableName;
            }

            var table = new TableClass
            {
                Name = shortName,
                Alias = alias,
                QualifiedName = qualified,
                ConnectionInfo = template.ConnectionInfo.Clone(true)
            };
            s.Database.AddTable(table, null);
            Changed(s, "Added table " + qualified + " as " + alias);

            var fromAlias = S(a, "link_from_alias");
            if (!string.IsNullOrWhiteSpace(fromAlias))
            {
                var link = BuildLink(fromAlias, StrList(a, "link_from_fields"), alias, StrList(a, "link_to_fields"), S(a, "join_type"));
                s.Database.AddTableLink(link);
                Changed(s, "Linked " + fromAlias + " -> " + alias);
            }

            var added = s.Database.Database.Tables.FindTableByAlias(alias);
            var fieldCount = added != null && added.DataFields != null ? added.DataFields.Count : 0;
            return Ok("Table " + qualified + " added as alias '" + alias + "' with " + fieldCount + " fields. Use list_report_tables to see them.");
        }

        private static TableLink BuildLink(string fromAlias, List<string> fromFields, string toAlias, List<string> toFields, string joinType)
        {
            if (fromFields.Count == 0 || fromFields.Count != toFields.Count)
                throw new ArgumentException("from and to field lists must be non-empty and the same length.");
            var src = new StringsClass();
            foreach (var f in fromFields) src.Add(QualifyLinkField(fromAlias, f));
            var dst = new StringsClass();
            foreach (var f in toFields) dst.Add(QualifyLinkField(toAlias, f));
            return new TableLinkClass
            {
                SourceTableAlias = fromAlias,
                TargetTableAlias = toAlias,
                SourceFieldNames = src,
                TargetFieldNames = dst,
                JoinType = ParseJoin(joinType)
            };
        }

        private static string QualifyLinkField(string alias, string field)
        {
            field = field.Trim().Trim('{', '}');
            return field.StartsWith(alias + ".", StringComparison.OrdinalIgnoreCase) ? field : alias + "." + field;
        }

        private object AddTableLink(ReportScope s, Dictionary<string, object> a)
        {
            var from = Req(a, "from_alias");
            var to = Req(a, "to_alias");
            var link = BuildLink(from, StrList(a, "from_fields"), to, StrList(a, "to_fields"), S(a, "join_type"));
            s.Database.AddTableLink(link);
            Changed(s, "Linked " + from + " -> " + to);
            return Ok("Link added.");
        }

        private object AddSqlCommand(ReportScope s, string name, string sql)
        {
            var template = FirstTable(s);
            s.Database.SetSQLCommandTable(template.ConnectionInfo.Clone(true), name, sql);
            Changed(s, "Added SQL command " + name);
            return Ok("SQL command '" + name + "' added. Use list_report_tables to see its fields.");
        }

        private object RemoveTable(ReportScope s, string alias)
        {
            s.Database.RemoveTable(alias);
            Changed(s, "Removed table " + alias);
            return Ok("Table " + alias + " removed.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Subreports
        // ══════════════════════════════════════════════════════════════════════

        private object CreateSubreport(Dictionary<string, object> a)
        {
            var name = Req(a, "name");
            if (SubreportNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return Error("A subreport named '" + name + "' already exists.");
            var main = Scope((string)null);
            var section = FindSection(main, Req(a, "section"));

            var templatePath = S(a, "template_path");
            string source;
            if (!string.IsNullOrWhiteSpace(templatePath))
            {
                if (!File.Exists(templatePath)) return Error("template_path not found: " + templatePath);
                source = templatePath;
            }
            else
            {
                source = BuildBlankReport(StrList(a, "keep_tables"));
            }

            _rcd.SubreportController.ImportSubreportEx(name, source, section,
                I(a, "left", 0), I(a, "top", 0), I(a, "width", 4320), I(a, "height", 720));
            Changed(main, "Created subreport '" + name + "' in " + section.Name);
            return Ok("Subreport '" + name + "' created in " + section.Name + ". Populate it with the other tools using subreport='" + name + "', then call set_subreport_links if it should be linked.");
        }

        /// <summary>
        /// Builds an empty report that shares the main report's connection: a copy of the source report
        /// with all objects, groups, sorts, selections, formulas and parameters removed.
        /// </summary>
        private string BuildBlankReport(List<string> keepTables)
        {
            var temp = Path.Combine(Path.GetTempPath(), "crv_blank_" + Guid.NewGuid().ToString("N") + ".rpt");
            var blank = new CrEngine.ReportDocument();
            try
            {
                blank.Load(SourcePath);
                var rcd = blank.ReportClientDocument;

                var objects = new List<ISCRReportObject>();
                foreach (ISCRReportObject o in rcd.ReportDefController.ReportObjectController.GetAllReportObjects()) objects.Add(o);
                foreach (var o in objects) { try { rcd.ReportDefController.ReportObjectController.Remove(o); } catch { } }

                var dd = rcd.DataDefController;
                while (dd.DataDefinition.Groups.Count > 0)
                {
                    var before = dd.DataDefinition.Groups.Count;
                    try { dd.GroupController.Remove(dd.DataDefinition.Groups[before - 1]); } catch { break; }
                    if (dd.DataDefinition.Groups.Count == before) break;
                }
                var sorts = new List<ISCRSort>();
                foreach (ISCRSort so in dd.DataDefinition.Sorts) sorts.Add(so);
                foreach (var so in sorts) { try { dd.SortController.Remove(so); } catch { } }
                try { dd.RecordFilterController.SetFormulaText(string.Empty); } catch { }
                try { dd.GroupFilterController.SetFormulaText(string.Empty); } catch { }

                // Formulas may depend on each other: retry until no progress.
                for (int pass = 0; pass < 5; pass++)
                {
                    var formulas = new List<ISCRField>();
                    foreach (ISCRField f in dd.DataDefinition.FormulaFields) formulas.Add(f);
                    if (formulas.Count == 0) break;
                    foreach (var f in formulas) { try { dd.FormulaFieldController.Remove(f); } catch { } }
                }
                var parameters = new List<ISCRField>();
                foreach (ISCRField p in dd.DataDefinition.ParameterFields) parameters.Add(p);
                foreach (var p in parameters) { try { dd.ParameterFieldController.Remove(p); } catch { } }

                if (keepTables != null && keepTables.Count > 0)
                {
                    var aliases = new List<string>();
                    foreach (ISCRTable t in rcd.DatabaseController.Database.Tables) aliases.Add(t.Alias);
                    foreach (var alias in aliases)
                    {
                        if (keepTables.Any(k => string.Equals(k, alias, StringComparison.OrdinalIgnoreCase))) continue;
                        try { rcd.DatabaseController.RemoveTable(alias); } catch { }
                    }
                }

                blank.SaveAs(temp);
            }
            finally
            {
                try { blank.Close(); blank.Dispose(); } catch { }
            }
            _tempFiles.Add(temp);
            return temp;
        }

        private object SetSubreportLinks(Dictionary<string, object> a)
        {
            var sub = Scope(Req(a, "subreport"));
            var mainFields = StrList(a, "main_fields");
            var subFields = StrList(a, "subreport_fields");
            if (mainFields.Count == 0 || mainFields.Count != subFields.Count)
                return Error("main_fields and subreport_fields must be non-empty and the same length.");

            var links = new SubreportLinksClass();
            for (int i = 0; i < mainFields.Count; i++)
            {
                var mf = NormalizeFieldRef(mainFields[i]);
                var sf = NormalizeFieldRef(subFields[i]);
                links.Add(new SubreportLinkClass
                {
                    MainReportFieldName = mf,
                    SubreportFieldName = sf,
                    LinkedParameterName = "Pm-" + mf.Trim('{', '}')
                });
            }
            _rcd.SubreportController.SetSubreportLinks(sub.Name, links);
            Changed(sub, "Set " + links.Count + " subreport link(s)");
            return Ok("Subreport links set.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Validation
        // ══════════════════════════════════════════════════════════════════════

        private object ValidateReport()
        {
            var problems = new List<string>();
            var scopes = new List<ReportScope> { Scope((string)null) };
            foreach (var n in SubreportNames()) scopes.Add(Scope(n));

            int checkedCount = 0;
            foreach (var s in scopes)
            {
                foreach (ISCRField f in s.DataDef.DataDefinition.FormulaFields)
                {
                    var ff = f as ISCRFormulaField;
                    if (ff == null) continue;
                    checkedCount++;
                    var err = RunCheck(s, ff.Name, ff.Text, ff.Syntax);
                    if (err != null) problems.Add(s.Label + " formula @" + ff.Name + ": " + err);
                }
                foreach (var pair in new[] { Tuple.Create("record selection", s.DataDef.RecordFilterController), Tuple.Create("group selection", s.DataDef.GroupFilterController) })
                {
                    var text = SafeGet(() => pair.Item2.GetFormulaText());
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    checkedCount++;
                    var err = RunCheck(s, "__ai_selection", text, CrFormulaSyntaxEnum.crFormulaSyntaxCrystal);
                    if (err != null) problems.Add(s.Label + " " + pair.Item1 + ": " + err);
                }
            }

            string render = "skipped (enable 'Validate by rendering' in AI Settings)";
            if (_settings.ValidateByRendering)
            {
                render = TestRender();
                if (!render.StartsWith("ok", StringComparison.OrdinalIgnoreCase)) problems.Add("Render: " + render);
            }

            return new Dictionary<string, object>
            {
                { "ok", problems.Count == 0 },
                { "formulasChecked", checkedCount },
                { "render", render },
                { "problems", problems }
            };
        }

        private string TestRender()
        {
            var temp = Path.Combine(Path.GetTempPath(), "crv_validate_" + Guid.NewGuid().ToString("N") + ".rpt");
            CrEngine.ReportDocument copy = null;
            try
            {
                _doc.SaveAs(temp);
                copy = new CrEngine.ReportDocument();
                copy.Load(temp);
                ApplyLogon(copy);
                foreach (CrEngine.ReportDocument sub in copy.Subreports) ApplyLogon(sub);
                using (var stream = copy.ExportToStream(CrShared.ExportFormatType.PortableDocFormat))
                {
                    return "ok (rendered " + stream.Length + " bytes)";
                }
            }
            catch (CrEngine.ParameterFieldCurrentValueException)
            {
                return "ok (formula/layout loaded; render skipped because the report requires parameter values)";
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try { if (copy != null) { copy.Close(); copy.Dispose(); } } catch { }
                try { File.Delete(temp); } catch { }
            }
        }

        /// <summary>Raised (on the Crystal worker thread) when a preview PDF has been rendered.</summary>
        public event Action<string> PdfRendered;

        private readonly object _imageLock = new object();
        private List<byte[]> _pendingPageImages;

        /// <summary>Returns and clears page images produced by the last render (null if none).</summary>
        public List<byte[]> TakePendingPageImages()
        {
            lock (_imageLock)
            {
                var r = _pendingPageImages;
                _pendingPageImages = null;
                return r;
            }
        }

        public Task<object> RenderPdfAsync(Dictionary<string, object> args)
        {
            return _worker.Invoke(() => RenderReportPdf(args ?? new Dictionary<string, object>()));
        }

        private object RenderReportPdf(Dictionary<string, object> a)
        {
            var result = new Dictionary<string, object>();
            var layoutIssues = LayoutIssues();
            result["layoutIssues"] = layoutIssues;

            var tempRpt = Path.Combine(Path.GetTempPath(), "crv_render_" + Guid.NewGuid().ToString("N") + ".rpt");
            var pdf = Path.Combine(Path.GetTempPath(), "crv_preview_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".pdf");
            CrEngine.ReportDocument copy = null;
            try
            {
                _doc.SaveAs(tempRpt);
                copy = new CrEngine.ReportDocument();
                copy.Load(tempRpt);
                ApplyLogon(copy);
                foreach (CrEngine.ReportDocument sub in copy.Subreports) ApplyLogon(sub);

                object po;
                var p = a.TryGetValue("parameters", out po) ? po as Dictionary<string, object> : null;
                if (p != null)
                    foreach (var kv in p) copy.SetParameterValue(kv.Key, kv.Value == null ? null : Convert.ToString(kv.Value, CultureInfo.InvariantCulture));

                byte[] bytes;
                using (var s = copy.ExportToStream(CrShared.ExportFormatType.PortableDocFormat))
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                File.WriteAllBytes(pdf, bytes);
                _tempFiles.Add(pdf);
                var raw = System.Text.Encoding.ASCII.GetString(bytes);
                result["ok"] = true;
                result["pdfPath"] = pdf;
                result["pageCount"] = System.Text.RegularExpressions.Regex.Matches(raw, @"/Type\s*/Page(?!s)").Count;
                result["bytes"] = bytes.Length;

                if (_settings.DataAccessMode == DataAccessModes.SchemaOnly)
                {
                    result["pageText"] = "withheld: data access mode is SchemaOnly, so rendered data is not sent to the AI. Rely on layoutIssues; the user can view the PDF.";
                }
                else
                {
                    string text;
                    using (var ts = copy.ExportToStream(CrShared.ExportFormatType.Text))
                    using (var sr = new StreamReader(ts))
                        text = sr.ReadToEnd();
                    var pages = text.Split('\f');
                    var maxPages = Math.Max(1, I(a, "max_pages", 2));
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < pages.Length && i < maxPages; i++)
                    {
                        sb.AppendLine("----- page " + (i + 1) + " -----");
                        foreach (var line in pages[i].Replace("\r", "").Split('\n'))
                            sb.AppendLine(_settings.IsSensitive(line) ? "<redacted line>" : line.TrimEnd());
                    }
                    var t = sb.ToString();
                    if (t.Length > 16000) t = t.Substring(0, 16000) + "\n…(truncated)";
                    result["pageText"] = t;
                }

                var h = PdfRendered;
                if (h != null) h(pdf);

                if (_settings.SendRenderedPagesToAi)
                {
                    try
                    {
                        var images = PdfPageImager.RenderPages(bytes, Math.Max(1, _settings.RenderedPagesMax));
                        lock (_imageLock) _pendingPageImages = images;
                        result["pageImages"] = images.Count + " page image(s) will be attached in the next message for visual review.";
                    }
                    catch (Exception ex)
                    {
                        result["pageImages"] = "unavailable: " + ex.GetType().Name + ": " + ex.Message;
                    }
                }
                else
                {
                    result["pageImages"] = "disabled (user has not enabled 'Send page images to AI').";
                }
            }
            catch (CrEngine.ParameterFieldCurrentValueException ex)
            {
                result["ok"] = false;
                result["error"] = "The report needs parameter values. Pass them in 'parameters'. " + ex.Message;
            }
            catch (Exception ex)
            {
                result["ok"] = false;
                result["error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try { if (copy != null) { copy.Close(); copy.Dispose(); } } catch { }
                try { File.Delete(tempRpt); } catch { }
            }
            return result;
        }

        private List<string> LayoutIssues()
        {
            var issues = new List<string>();
            var scopes = new List<ReportScope> { Scope((string)null) };
            foreach (var n in SubreportNames()) scopes.Add(Scope(n));
            foreach (var s in scopes)
            {
                foreach (ISCRArea area in s.ReportDef.ReportDefinition.Areas)
                    foreach (Section sec in area.Sections)
                    {
                        if (sec.Format != null && sec.Format.EnableSuppress) continue;
                        var objs = new List<ISCRReportObject>();
                        foreach (ISCRReportObject o in sec.ReportObjects)
                        {
                            if (o.Format != null && o.Format.EnableSuppress) continue;
                            objs.Add(o);
                            if (o.Left + o.Width > sec.Width)
                                issues.Add(s.Label + " " + sec.Name + ": '" + o.Name + "' extends past section width (" + (o.Left + o.Width) + " > " + sec.Width + ")");
                            var isLineOrBox = o.Kind == CrReportObjectKindEnum.crReportObjectKindLine || o.Kind == CrReportObjectKindEnum.crReportObjectKindBox;
                            if (!isLineOrBox && o.Top + o.Height > sec.Height)
                                issues.Add(s.Label + " " + sec.Name + ": '" + o.Name + "' extends below section height");
                            if ((o.Kind == CrReportObjectKindEnum.crReportObjectKindText || o.Kind == CrReportObjectKindEnum.crReportObjectKindField) && o.Height < 180)
                                issues.Add(s.Label + " " + sec.Name + ": '" + o.Name + "' height " + o.Height + " twips may clip text");
                        }
                        var content = objs.Where(o => o.Kind != CrReportObjectKindEnum.crReportObjectKindLine && o.Kind != CrReportObjectKindEnum.crReportObjectKindBox).ToList();
                        for (int i = 0; i < content.Count; i++)
                            for (int j = i + 1; j < content.Count; j++)
                            {
                                var x = content[i]; var y = content[j];
                                if (x.Left < y.Left + y.Width && y.Left < x.Left + x.Width && x.Top < y.Top + y.Height && y.Top < x.Top + x.Height)
                                    issues.Add(s.Label + " " + sec.Name + ": '" + x.Name + "' overlaps '" + y.Name + "'");
                            }
                    }
            }
            return issues;
        }

        private void ApplyLogon(CrEngine.ReportDocument doc)
        {
            foreach (CrEngine.Table t in doc.Database.Tables)
            {
                var li = t.LogOnInfo;
                if (!string.IsNullOrWhiteSpace(_settings.CrystalServer)) li.ConnectionInfo.ServerName = _settings.CrystalServer;
                if (!string.IsNullOrWhiteSpace(_settings.CrystalDatabase)) li.ConnectionInfo.DatabaseName = _settings.CrystalDatabase;
                li.ConnectionInfo.IntegratedSecurity = _settings.CrystalIntegratedSecurity;
                if (!_settings.CrystalIntegratedSecurity)
                {
                    li.ConnectionInfo.UserID = _settings.CrystalUser;
                    li.ConnectionInfo.Password = _settings.CrystalPassword;
                }
                t.ApplyLogOnInfo(li);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // Helpers
        // ══════════════════════════════════════════════════════════════════════

        private static string NormalizeFieldRef(string field)
        {
            field = (field ?? string.Empty).Trim();
            if (!field.StartsWith("{")) field = "{" + field;
            if (!field.EndsWith("}")) field = field + "}";
            return field;
        }

        private static List<string> StringsToList(Strings strings)
        {
            var list = new List<string>();
            if (strings == null) return list;
            foreach (string s in strings) list.Add(s);
            return list;
        }

        private static string S(Dictionary<string, object> a, string key)
        {
            object v;
            return a.TryGetValue(key, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        }

        private static string Req(Dictionary<string, object> a, string key)
        {
            var v = S(a, key);
            if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException("Missing required argument: " + key);
            return v;
        }

        private static string ReqAllowEmpty(Dictionary<string, object> a, string key)
        {
            if (!a.ContainsKey(key)) throw new ArgumentException("Missing required argument: " + key);
            return S(a, key) ?? string.Empty;
        }

        private static int I(Dictionary<string, object> a, string key, int defaultValue)
        {
            object v;
            if (!a.TryGetValue(key, out v) || v == null) return defaultValue;
            try { return Convert.ToInt32(Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture))); }
            catch { return defaultValue; }
        }

        private static bool B(Dictionary<string, object> a, string key)
        {
            object v;
            if (!a.TryGetValue(key, out v) || v == null) return false;
            if (v is bool) return (bool)v;
            bool b;
            return bool.TryParse(Convert.ToString(v), out b) && b;
        }

        private static List<string> StrList(Dictionary<string, object> a, string key)
        {
            var list = new List<string>();
            object v;
            if (!a.TryGetValue(key, out v) || v == null) return list;
            var arr = v as object[];
            if (arr != null) { foreach (var x in arr) if (x != null) list.Add(Convert.ToString(x)); }
            else if (v is System.Collections.IEnumerable && !(v is string)) { foreach (var x in (System.Collections.IEnumerable)v) if (x != null) list.Add(Convert.ToString(x)); }
            else list.Add(Convert.ToString(v));
            return list;
        }

        private static string SafeGet(Func<string> f)
        {
            try { return f(); } catch { return null; }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        private static Dictionary<string, object> Ok(string message)
        {
            return new Dictionary<string, object> { { "ok", true }, { "message", message } };
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object> { { "ok", false }, { "error", message } };
        }

        private static string Json(object o)
        {
            return OpenAiClient.CreateSerializer().Serialize(o);
        }
    }
}
