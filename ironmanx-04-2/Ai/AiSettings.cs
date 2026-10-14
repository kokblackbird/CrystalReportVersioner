using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace ironmanx_04_2.Ai
{
    public static class DataAccessModes
    {
        /// <summary>Only table/column names, types, keys and links. No row data ever leaves the database.</summary>
        public const string SchemaOnly = "SchemaOnly";
        /// <summary>Schema plus a small random sample of rows per table (sensitive columns/values redacted).</summary>
        public const string RandomSample = "RandomSample";
        /// <summary>Schema, samples and AI-written read-only SELECT queries (sensitive columns/values redacted).</summary>
        public const string ReadOnlyQueries = "ReadOnlyQueries";
    }

    /// <summary>
    /// AI agent configuration persisted to %APPDATA%\CrystalReportVersioner\ai-settings.json.
    /// Secrets (API key, DB connection string, Crystal password) are encrypted with DPAPI for the current Windows user.
    /// </summary>
    public sealed class AiSettings
    {
        // ── OpenAI ────────────────────────────────────────────────────────────
        public string OpenAiApiKeyProtected { get; set; } = string.Empty;
        public string OpenAiBaseUrl { get; set; } = "https://api.openai.com/v1";
        public string OpenAiModel { get; set; } = "gpt-5";
        public string OpenAiOrganization { get; set; } = string.Empty;
        public string OpenAiProject { get; set; } = string.Empty;
        public double Temperature { get; set; } = 0.2;
        /// <summary>default | minimal | low | medium | high — only sent to reasoning models (gpt-5, o-series).</summary>
        public string ReasoningEffort { get; set; } = "medium";
        public int MaxIterations { get; set; } = 40;
        public int RequestTimeoutSeconds { get; set; } = 180;
        public string ExtraInstructions { get; set; } = string.Empty;
        /// <summary>Reference documents (full paths) available to the agent in every session.</summary>
        public System.Collections.Generic.List<string> ResourceFiles { get; set; } = new System.Collections.Generic.List<string>();

        // ── Live database (schema tools) ──────────────────────────────────────
        /// <summary>SqlClient | OleDb | Odbc</summary>
        public string DbProvider { get; set; } = "SqlClient";
        public string DbConnectionStringProtected { get; set; } = string.Empty;
        /// <summary>SchemaOnly | RandomSample | ReadOnlyQueries — controls how much row data the AI may see.</summary>
        public string DataAccessMode { get; set; } = DataAccessModes.SchemaOnly;
        public int SqlQueryMaxRows { get; set; } = 25;
        /// <summary>Keywords (one per line or comma separated). Matching tables/columns are hidden and matching values are redacted.</summary>
        public string SensitiveKeywords { get; set; } = "password\nssn\nsocial\nsalary\ncredit\ncard\nbank\naccount_number\ndob\nbirth\ntax";

        [ScriptIgnore]
        public string[] SensitiveKeywordList
        {
            get
            {
                return (SensitiveKeywords ?? string.Empty)
                    .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
        }

        public bool IsSensitive(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var k in SensitiveKeywordList)
                if (text.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }
        public int SqlCommandTimeoutSeconds { get; set; } = 30;

        // ── Crystal logon (validation by rendering) ───────────────────────────
        public bool ValidateByRendering { get; set; } = false;
        /// <summary>Opt-in: send rendered PDF pages as images to the (vision-capable) model. Images may contain real report data.</summary>
        public bool SendRenderedPagesToAi { get; set; } = false;
        public int RenderedPagesMax { get; set; } = 2;
        public string CrystalServer { get; set; } = string.Empty;
        public string CrystalDatabase { get; set; } = string.Empty;
        public string CrystalUser { get; set; } = string.Empty;
        public string CrystalPasswordProtected { get; set; } = string.Empty;
        public bool CrystalIntegratedSecurity { get; set; } = true;

        // ── Secret accessors (not serialized) ─────────────────────────────────
        [ScriptIgnore]
        public string OpenAiApiKey
        {
            get { return Unprotect(OpenAiApiKeyProtected); }
            set { OpenAiApiKeyProtected = Protect(value); }
        }

        [ScriptIgnore]
        public string DbConnectionString
        {
            get { return Unprotect(DbConnectionStringProtected); }
            set { DbConnectionStringProtected = Protect(value); }
        }

        [ScriptIgnore]
        public string CrystalPassword
        {
            get { return Unprotect(CrystalPasswordProtected); }
            set { CrystalPasswordProtected = Protect(value); }
        }

        [ScriptIgnore]
        public bool IsOpenAiConfigured
        {
            get { return !string.IsNullOrWhiteSpace(OpenAiApiKey) && !string.IsNullOrWhiteSpace(OpenAiModel); }
        }

        [ScriptIgnore]
        public bool IsDatabaseConfigured
        {
            get { return !string.IsNullOrWhiteSpace(DbConnectionString); }
        }

        public static string SettingsPath
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrystalReportVersioner");
                return Path.Combine(dir, "ai-settings.json");
            }
        }

        public static AiSettings Load()
        {
            try
            {
                var path = SettingsPath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path, Encoding.UTF8);
                    var s = new JavaScriptSerializer().Deserialize<AiSettings>(json);
                    if (s != null)
                    {
                        // Allow the environment variable to supply the key when none is saved.
                        if (string.IsNullOrWhiteSpace(s.OpenAiApiKey)) s.TryApplyEnvironmentKey();
                        return s;
                    }
                }
            }
            catch { /* fall through to defaults */ }
            var d = new AiSettings();
            d.TryApplyEnvironmentKey();
            return d;
        }

        public void Save()
        {
            var path = SettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var json = new JavaScriptSerializer().Serialize(this);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        private void TryApplyEnvironmentKey()
        {
            var env = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) OpenAiApiKeyProtected = Protect(env);
        }

        private static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string cipher)
        {
            if (string.IsNullOrEmpty(cipher)) return string.Empty;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(cipher), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return string.Empty; }
        }
    }
}
