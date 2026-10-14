using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ironmanx_04_2.Ai
{
    /// <summary>
    /// Tool-calling agent loop: sends the conversation to OpenAI, executes requested tools
    /// against the open report, feeds results back, and repeats until the model answers without tools.
    /// The conversation is kept so the user can send follow-up instructions.
    /// </summary>
    public sealed class ReportAgent
    {
        private readonly AiSettings _settings;
        private readonly OpenAiClient _client;
        private readonly ReportAgentTools _tools;
        private readonly List<Dictionary<string, object>> _messages = new List<Dictionary<string, object>>();
        private readonly IList<object> _toolDefs;

        public event Action<string> Progress;

        public int TotalPromptTokens { get; private set; }
        public int TotalCompletionTokens { get; private set; }

        public ReportAgent(AiSettings settings, ReportAgentTools tools)
        {
            _settings = settings;
            _client = new OpenAiClient(settings);
            _tools = tools;
            _toolDefs = tools.GetToolDefinitions();
            _messages.Add(Msg("system", BuildSystemPrompt()));
        }

        private string BuildSystemPrompt()
        {
            var prompt =
@"You are an expert SAP Crystal Reports developer working inside 'Crystal Report Versioner'.
You edit an open .rpt file ONLY through the provided tools. Changes are saved by the user to a NEW file; the original is never overwritten.

Workflow:
1. Call get_report_overview (and list_report_tables when you need field names) before changing anything.
2. When you need to know the database (tables, columns, relationships) use db_list_tables and db_describe_table. Row-data tools (db_sample_table, db_query) exist only if the user's data access mode allows them; never ask for or guess confidential values. '<redacted>' values and hidden columns are intentional — do not try to work around them.
3. Make the smallest set of changes that satisfies the request. Prefer modifying existing objects over recreating them.
review layoutIssues and pageText (and the attached page images when provided)
5. Finish with a short summary of every change you made and anything the user must do manually.

Crystal specifics:
- Positions/sizes are twips (1440 = 1 inch, 20 = 1 point). Keep objects inside the section width and avoid overlapping existing objects; enlarge sections with set_section_properties when needed.
- Field references use formula form: {Table.Field}, {@Formula}, {?Parameter}, {#RunningTotal}.
- Default to Crystal syntax for formulas. Summaries can be written as formulas, e.g. Sum({Orders.Amount}, {Orders.CustomerID}).
- New tables reuse the report's existing connection; link them with add_table / add_table_link.
- Subreports: create_subreport makes a blank subreport (same connection) or imports a template .rpt. Then call the other tools with subreport=<name> to add tables, fields, formulas, selection and layout inside it, and set_subreport_links to link it to the main report.
- If a tool returns ok=false, read the error, correct the input and retry; do not repeat the same failing call.
- Never invent object, section, table or field names; read them from the tools.";
            if (!string.IsNullOrWhiteSpace(_settings.ExtraInstructions))
                prompt += "\n\nAdditional instructions from the user:\n" + _settings.ExtraInstructions.Trim();
            var resources = _tools.Resources.BuildPromptSection();
            if (!string.IsNullOrEmpty(resources))
                prompt += "\n\n" + resources + "\nUse search_resources / read_resource to consult documents that are not inlined above before making related changes.";
            return prompt;
        }

        public async Task<string> RunAsync(string userRequest, CancellationToken ct)
        {
            _messages.Add(Msg("user", userRequest));
            var maxIterations = Math.Max(1, _settings.MaxIterations);

            for (int i = 0; i < maxIterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                Report("Thinking (step " + (i + 1) + ")...");
                var result = await _client.ChatAsync(_messages, _toolDefs, ct).ConfigureAwait(false);
                TotalPromptTokens += result.PromptTokens;
                TotalCompletionTokens += result.CompletionTokens;
                _messages.Add(result.AssistantMessage);

                if (!string.IsNullOrWhiteSpace(result.Content) && result.ToolCalls.Count > 0)
                    Report("AI: " + result.Content.Trim());

                if (result.ToolCalls.Count == 0)
                    return result.Content ?? "(no response)";

                foreach (var call in result.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    Report("→ " + call.Name + " " + Shorten(call.ArgumentsJson, 300));
                    var output = await _tools.ExecuteAsync(call.Name, call.ArgumentsJson).ConfigureAwait(false);
                    Report("  ← " + Shorten(output, 400));
                    _messages.Add(new Dictionary<string, object>
                    {
                        { "role", "tool" },
                        { "tool_call_id", call.Id },
                        { "content", output }
                    });
                }

                var images = _tools.TakePendingPageImages();
                if (images != null && images.Count > 0)
                {
                    StripOldImages();
                    var parts = new List<object>
                    {
                        new Dictionary<string, object> { { "type", "text" }, { "text", "Rendered report pages (Crystal runtime PDF, first " + images.Count + " page(s)). Review them visually for alignment, overlap, truncation, spacing, fonts and overall readability, then fix problems with the tools." } }
                    };
                    foreach (var img in images)
                        parts.Add(new Dictionary<string, object>
                        {
                            { "type", "image_url" },
                            { "image_url", new Dictionary<string, object> { { "url", "data:image/png;base64," + Convert.ToBase64String(img) }, { "detail", "high" } } }
                        });
                    _messages.Add(new Dictionary<string, object> { { "role", "user" }, { "content", parts } });
                    Report("  (attached " + images.Count + " rendered page image(s) for visual review)");
                }
            }
            return "Stopped after reaching the maximum number of agent steps (" + maxIterations + "). Send a follow-up message to continue.";
        }

        /// <summary>Replaces earlier page images with a placeholder so only the latest render is resent.</summary>
        private void StripOldImages()
        {
            for (int i = 0; i < _messages.Count; i++)
            {
                object c;
                if (_messages[i].TryGetValue("content", out c) && c is List<object>)
                    _messages[i] = Msg("user", "(Earlier rendered page images removed; superseded by a newer render.)");
            }
        }

        private void Report(string text)
        {
            var h = Progress;
            if (h != null) h(text);
        }

        private static Dictionary<string, object> Msg(string role, string content)
        {
            return new Dictionary<string, object> { { "role", role }, { "content", content } };
        }

        private static string Shorten(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
