using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ironmanx_04_2.Ai
{
    /// <summary>A tool call requested by the model.</summary>
    public sealed class OpenAiToolCall
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ArgumentsJson { get; set; }
    }

    /// <summary>Assistant reply from a chat completion.</summary>
    public sealed class OpenAiChatResult
    {
        public string Content { get; set; }
        public List<OpenAiToolCall> ToolCalls { get; } = new List<OpenAiToolCall>();
        public string FinishReason { get; set; }
        /// <summary>The raw assistant message, to append back to the conversation.</summary>
        public Dictionary<string, object> AssistantMessage { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
    }

    /// <summary>
    /// Minimal OpenAI Chat Completions client with tool (function) calling support.
    /// Uses HttpClient and JavaScriptSerializer so no NuGet packages are required on .NET Framework 4.8.
    /// </summary>
    public sealed class OpenAiClient
    {
        private static readonly HttpClient Http = CreateHttpClient();
        private readonly AiSettings _settings;

        public OpenAiClient(AiSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        private static HttpClient CreateHttpClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        }

        internal static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
        }

        public async Task<string> PingAsync(CancellationToken ct)
        {
            var messages = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "role", "user" }, { "content", "Reply with the single word: ready" } }
            };
            var result = await ChatAsync(messages, null, ct).ConfigureAwait(false);
            return (result.Content ?? string.Empty).Trim();
        }

        public async Task<OpenAiChatResult> ChatAsync(List<Dictionary<string, object>> messages, IList<object> tools, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_settings.OpenAiApiKey))
                throw new InvalidOperationException("OpenAI API key is not configured. Open AI Settings.");

            var body = new Dictionary<string, object>
            {
                { "model", _settings.OpenAiModel },
                { "messages", messages }
            };
            if (SupportsTemperature(_settings.OpenAiModel)) body["temperature"] = _settings.Temperature;
            else if (!string.IsNullOrWhiteSpace(_settings.ReasoningEffort) && !string.Equals(_settings.ReasoningEffort, "default", StringComparison.OrdinalIgnoreCase))
                body["reasoning_effort"] = _settings.ReasoningEffort.ToLowerInvariant();
            if (tools != null && tools.Count > 0)
            {
                body["tools"] = tools;
                body["tool_choice"] = "auto";
                body["parallel_tool_calls"] = false;
            }

            var json = CreateSerializer().Serialize(body);
            var url = (_settings.OpenAiBaseUrl ?? "https://api.openai.com/v1").TrimEnd('/') + "/chat/completions";

            const int maxAttempts = 4;
            for (int attempt = 1; ; attempt++)
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(10, _settings.RequestTimeoutSeconds))))
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenAiApiKey);
                    if (!string.IsNullOrWhiteSpace(_settings.OpenAiOrganization)) req.Headers.Add("OpenAI-Organization", _settings.OpenAiOrganization);
                    if (!string.IsNullOrWhiteSpace(_settings.OpenAiProject)) req.Headers.Add("OpenAI-Project", _settings.OpenAiProject);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");

                    HttpResponseMessage resp;
                    try
                    {
                        resp = await Http.SendAsync(req, linked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        if (attempt < maxAttempts) continue;
                        throw new TimeoutException("OpenAI request timed out.");
                    }

                    using (resp)
                    {
                        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var code = (int)resp.StatusCode;
                        if (code == 429 || code >= 500)
                        {
                            if (attempt < maxAttempts)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct).ConfigureAwait(false);
                                continue;
                            }
                        }
                        if (!resp.IsSuccessStatusCode)
                            throw new InvalidOperationException("OpenAI error " + code + ": " + ExtractError(text));
                        return Parse(text);
                    }
                }
            }
        }

        /// <summary>Returns the chat-capable model IDs available to this API key, newest first.</summary>
        public async Task<List<string>> ListModelsAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(_settings.OpenAiApiKey))
                throw new InvalidOperationException("OpenAI API key is not configured.");
            var url = (_settings.OpenAiBaseUrl ?? "https://api.openai.com/v1").TrimEnd('/') + "/models";
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.OpenAiApiKey);
                if (!string.IsNullOrWhiteSpace(_settings.OpenAiOrganization)) req.Headers.Add("OpenAI-Organization", _settings.OpenAiOrganization);
                if (!string.IsNullOrWhiteSpace(_settings.OpenAiProject)) req.Headers.Add("OpenAI-Project", _settings.OpenAiProject);
                using (var resp = await Http.SendAsync(req, ct).ConfigureAwait(false))
                {
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new InvalidOperationException("OpenAI error " + (int)resp.StatusCode + ": " + ExtractError(text));
                    var root = CreateSerializer().DeserializeObject(text) as Dictionary<string, object>;
                    var items = new List<KeyValuePair<string, long>>();
                    object data;
                    if (root != null && root.TryGetValue("data", out data) && data is object[])
                    {
                        foreach (var o in (object[])data)
                        {
                            var d = o as Dictionary<string, object>;
                            if (d == null || !d.ContainsKey("id")) continue;
                            var id = Convert.ToString(d["id"]);
                            if (!IsChatModel(id)) continue;
                            long created = 0;
                            if (d.ContainsKey("created")) try { created = Convert.ToInt64(d["created"]); } catch { }
                            items.Add(new KeyValuePair<string, long>(id, created));
                        }
                    }
                    return items.OrderByDescending(i => i.Value).Select(i => i.Key).ToList();
                }
            }
        }

        private static bool IsChatModel(string id)
        {
            var m = (id ?? string.Empty).ToLowerInvariant();
            if (!(m.StartsWith("gpt-") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") || m.StartsWith("chatgpt"))) return false;
            foreach (var bad in new[] { "audio", "realtime", "transcribe", "tts", "image", "search", "instruct", "embedding", "codex" })
                if (m.Contains(bad)) return false;
            return true;
        }

        private static bool SupportsTemperature(string model)
        {
            // Reasoning models (o1/o3/o4 families, gpt-5) reject custom temperature values.
            if (string.IsNullOrEmpty(model)) return true;
            var m = model.ToLowerInvariant();
            return !(m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") || m.StartsWith("gpt-5") || m.StartsWith("gpt-6"));
        }

        private static string ExtractError(string body)
        {
            try
            {
                var root = CreateSerializer().DeserializeObject(body) as Dictionary<string, object>;
                object err;
                if (root != null && root.TryGetValue("error", out err))
                {
                    var e = err as Dictionary<string, object>;
                    object msg;
                    if (e != null && e.TryGetValue("message", out msg)) return Convert.ToString(msg);
                }
            }
            catch { }
            return body != null && body.Length > 500 ? body.Substring(0, 500) : body;
        }

        private static OpenAiChatResult Parse(string body)
        {
            var root = (Dictionary<string, object>)CreateSerializer().DeserializeObject(body);
            var choices = (object[])root["choices"];
            var choice = (Dictionary<string, object>)choices[0];
            var message = (Dictionary<string, object>)choice["message"];

            var result = new OpenAiChatResult
            {
                Content = message.ContainsKey("content") ? message["content"] as string : null,
                FinishReason = choice.ContainsKey("finish_reason") ? choice["finish_reason"] as string : null
            };

            var assistant = new Dictionary<string, object> { { "role", "assistant" }, { "content", result.Content } };

            object tcObj;
            if (message.TryGetValue("tool_calls", out tcObj) && tcObj is object[])
            {
                var toolCalls = (object[])tcObj;
                var echo = new List<object>();
                foreach (Dictionary<string, object> tc in toolCalls)
                {
                    var fn = (Dictionary<string, object>)tc["function"];
                    var call = new OpenAiToolCall
                    {
                        Id = tc["id"] as string,
                        Name = fn["name"] as string,
                        ArgumentsJson = fn.ContainsKey("arguments") ? fn["arguments"] as string : "{}"
                    };
                    result.ToolCalls.Add(call);
                    echo.Add(new Dictionary<string, object>
                    {
                        { "id", call.Id },
                        { "type", "function" },
                        { "function", new Dictionary<string, object> { { "name", call.Name }, { "arguments", call.ArgumentsJson ?? "{}" } } }
                    });
                }
                if (echo.Count > 0) assistant["tool_calls"] = echo;
            }
            result.AssistantMessage = assistant;

            object usageObj;
            if (root.TryGetValue("usage", out usageObj) && usageObj is Dictionary<string, object>)
            {
                var usage = (Dictionary<string, object>)usageObj;
                object v;
                if (usage.TryGetValue("prompt_tokens", out v)) result.PromptTokens = Convert.ToInt32(v);
                if (usage.TryGetValue("completion_tokens", out v)) result.CompletionTokens = Convert.ToInt32(v);
            }
            return result;
        }

        // ── Tool schema helpers ───────────────────────────────────────────────

        public static Dictionary<string, object> Tool(string name, string description, Dictionary<string, object> properties, params string[] required)
        {
            var parameters = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", properties ?? new Dictionary<string, object>() }
            };
            if (required != null && required.Length > 0) parameters["required"] = required;
            return new Dictionary<string, object>
            {
                { "type", "function" },
                { "function", new Dictionary<string, object>
                    {
                        { "name", name },
                        { "description", description },
                        { "parameters", parameters }
                    }
                }
            };
        }

        public static Dictionary<string, object> Str(string description, params string[] enumValues)
        {
            var d = new Dictionary<string, object> { { "type", "string" }, { "description", description } };
            if (enumValues != null && enumValues.Length > 0) d["enum"] = enumValues;
            return d;
        }

        public static Dictionary<string, object> Int(string description)
        {
            return new Dictionary<string, object> { { "type", "integer" }, { "description", description } };
        }

        public static Dictionary<string, object> Num(string description)
        {
            return new Dictionary<string, object> { { "type", "number" }, { "description", description } };
        }

        public static Dictionary<string, object> Bool(string description)
        {
            return new Dictionary<string, object> { { "type", "boolean" }, { "description", description } };
        }

        public static Dictionary<string, object> StrArray(string description)
        {
            return new Dictionary<string, object>
            {
                { "type", "array" },
                { "description", description },
                { "items", new Dictionary<string, object> { { "type", "string" } } }
            };
        }

        public static Dictionary<string, object> Props(params object[] nameValuePairs)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2) d[(string)nameValuePairs[i]] = nameValuePairs[i + 1];
            return d;
        }
    }
}
