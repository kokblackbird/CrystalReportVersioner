using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Data.OleDb;
using System.Data.SqlClient;
using System.Linq;
using System.Text.RegularExpressions;

namespace ironmanx_04_2.Ai
{
    /// <summary>
    /// Read-only live database introspection used by the agent so it "knows" the database:
    /// list tables/views, describe columns/keys/foreign keys, and run bounded SELECT queries.
    /// </summary>
    public sealed class DatabaseSchemaTools
    {
        private static readonly Regex SafeIdentifier = new Regex(@"^[\w\.\[\]""\s\$#@-]+$", RegexOptions.Compiled);
        private static readonly Regex ForbiddenSql = new Regex(
            @"\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute|grant|revoke|deny|backup|restore|shutdown|dbcc|bulk|openrowset|opendatasource|into|xp_\w+|sp_\w+)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly AiSettings _settings;

        public DatabaseSchemaTools(AiSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public bool IsConfigured { get { return _settings.IsDatabaseConfigured; } }

        private DbConnection CreateConnection()
        {
            var cs = _settings.DbConnectionString;
            if (string.IsNullOrWhiteSpace(cs)) throw new InvalidOperationException("No database connection string configured in AI Settings.");
            switch ((_settings.DbProvider ?? "SqlClient").ToLowerInvariant())
            {
                case "oledb": return new OleDbConnection(cs);
                case "odbc": return new OdbcConnection(cs);
                default: return new SqlConnection(cs);
            }
        }

        private bool IsSqlServer
        {
            get { return string.Equals(_settings.DbProvider, "SqlClient", StringComparison.OrdinalIgnoreCase); }
        }

        public string TestConnection()
        {
            try
            {
                using (var conn = CreateConnection())
                {
                    conn.Open();
                    return "Database OK: " + conn.DataSource + " / " + conn.Database + " (server " + conn.ServerVersion + ")";
                }
            }
            catch (Exception ex)
            {
                return "Database failed: " + ex.Message;
            }
        }

        /// <summary>Lists tables and views. Optional case-insensitive substring filter.</summary>
        public object ListTables(string filter, int max)
        {
            if (max <= 0) max = 300;
            using (var conn = CreateConnection())
            {
                conn.Open();
                var schema = conn.GetSchema("Tables");
                var nameCol = FirstColumn(schema, "TABLE_NAME");
                var schemaCol = FirstColumn(schema, "TABLE_SCHEMA", "TABLE_SCHEM", "TABLE_OWNER");
                var typeCol = FirstColumn(schema, "TABLE_TYPE");
                var list = new List<object>();
                int total = 0, hidden = 0;
                foreach (DataRow row in schema.Rows)
                {
                    var name = nameCol != null ? Convert.ToString(row[nameCol]) : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    var sch = schemaCol != null ? Convert.ToString(row[schemaCol]) : null;
                    var type = typeCol != null ? Convert.ToString(row[typeCol]) : null;
                    if (type != null && type.IndexOf("SYSTEM", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (sch != null && (sch.Equals("sys", StringComparison.OrdinalIgnoreCase) || sch.Equals("INFORMATION_SCHEMA", StringComparison.OrdinalIgnoreCase))) continue;
                    var qualified = string.IsNullOrEmpty(sch) ? name : sch + "." + name;
                    if (!string.IsNullOrWhiteSpace(filter) && qualified.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (_settings.IsSensitive(qualified)) { hidden++; continue; }
                    total++;
                    if (list.Count < max) list.Add(new Dictionary<string, object> { { "name", qualified }, { "type", type } });
                }
                return new Dictionary<string, object>
                {
                    { "database", conn.Database },
                    { "dataSource", conn.DataSource },
                    { "totalMatches", total },
                    { "hiddenAsSensitive", hidden },
                    { "dataAccessMode", _settings.DataAccessMode },
                    { "tables", list }
                };
            }
        }

        /// <summary>Describes columns, primary key and (SQL Server) foreign keys for a table or view.</summary>
        public object DescribeTable(string tableName)
        {
            if (string.IsNullOrWhiteSpace(tableName) || !SafeIdentifier.IsMatch(tableName))
                throw new ArgumentException("Invalid table name: " + tableName);
            if (_settings.IsSensitive(tableName))
                throw new InvalidOperationException("Table '" + tableName + "' is blocked by the sensitive-keyword list in AI Settings.");
                using (var conn = CreateConnection())
                {
                    conn.Open();
                    var columns = new List<object>();
                    int hiddenColumns = 0;
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT * FROM " + QuoteName(tableName) + " WHERE 1=0";
                    cmd.CommandTimeout = _settings.SqlCommandTimeoutSeconds;
                    using (var reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo))
                    {
                        var st = reader.GetSchemaTable();
                        if (st != null)
                        {
                            foreach (DataRow r in st.Rows)
                            {
                                var dataType = r["DataType"] as Type;
                                if (_settings.IsSensitive(Convert.ToString(r["ColumnName"]))) { hiddenColumns++; continue; }
                                columns.Add(new Dictionary<string, object>
                                {
                                    { "name", Convert.ToString(r["ColumnName"]) },
                                    { "type", dataType != null ? dataType.Name : null },
                                    { "dbType", st.Columns.Contains("DataTypeName") ? Convert.ToString(r["DataTypeName"]) : null },
                                    { "size", SafeInt(r, "ColumnSize") },
                                    { "nullable", SafeBool(r, "AllowDBNull") },
                                    { "isKey", SafeBool(r, "IsKey") }
                                });
                            }
                        }
                    }
                }

                var result = new Dictionary<string, object> { { "table", tableName }, { "columns", columns }, { "hiddenSensitiveColumns", hiddenColumns } };
                if (IsSqlServer)
                {
                    try { result["foreignKeys"] = GetSqlServerForeignKeys(conn, tableName); }
                    catch (Exception ex) { result["foreignKeysError"] = ex.Message; }
                }
                return result;
            }
        }

        private List<object> GetSqlServerForeignKeys(DbConnection conn, string tableName)
        {
            const string sql = @"
SELECT fk.name AS fk_name,
       SCHEMA_NAME(tp.schema_id) + '.' + tp.name AS parent_table, cp.name AS parent_column,
       SCHEMA_NAME(tr.schema_id) + '.' + tr.name AS referenced_table, cr.name AS referenced_column
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables tp ON tp.object_id = fkc.parent_object_id
JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
JOIN sys.tables tr ON tr.object_id = fkc.referenced_object_id
JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
WHERE fkc.parent_object_id = OBJECT_ID(@t) OR fkc.referenced_object_id = OBJECT_ID(@t)
ORDER BY fk.name";
            var list = new List<object>();
            using (var cmd = (SqlCommand)conn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.CommandTimeout = _settings.SqlCommandTimeoutSeconds;
                cmd.Parameters.AddWithValue("@t", tableName);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        var from = r.GetString(1) + "." + r.GetString(2);
                        var to = r.GetString(3) + "." + r.GetString(4);
                        if (_settings.IsSensitive(from) || _settings.IsSensitive(to)) continue;
                        list.Add(new Dictionary<string, object>
                        {
                            { "name", r.GetString(0) },
                            { "from", from },
                            { "to", to }
                        });
                    }
                }
            }
            return list;
        }

        public bool SamplingAllowed
        {
            get { return _settings.DataAccessMode == DataAccessModes.RandomSample || _settings.DataAccessMode == DataAccessModes.ReadOnlyQueries; }
        }

        public bool QueriesAllowed
        {
            get { return _settings.DataAccessMode == DataAccessModes.ReadOnlyQueries; }
        }

        /// <summary>Returns a small random sample of rows from a table (sensitive columns/values redacted).</summary>
        public object SampleTable(string tableName, int maxRows)
        {
            if (!SamplingAllowed) throw new InvalidOperationException("Data access mode is Schema only: row data is not available. Work from column names/types.");
            if (string.IsNullOrWhiteSpace(tableName) || !SafeIdentifier.IsMatch(tableName)) throw new ArgumentException("Invalid table name: " + tableName);
            if (_settings.IsSensitive(tableName)) throw new InvalidOperationException("Table '" + tableName + "' is blocked by the sensitive-keyword list in AI Settings.");
            var limit = maxRows <= 0 ? _settings.SqlQueryMaxRows : Math.Min(maxRows, _settings.SqlQueryMaxRows);
            if (IsSqlServer)
                return Execute("SELECT TOP " + limit + " * FROM " + QuoteName(tableName) + " ORDER BY NEWID()", limit, false);
            // Generic providers: reservoir-sample from the first rows read.
            return Execute("SELECT * FROM " + QuoteName(tableName), limit, true);
        }

        /// <summary>Runs a bounded, read-only SELECT. Executed inside a transaction that is always rolled back.</summary>
        public object RunQuery(string sql, int maxRows)
        {
            if (!QueriesAllowed) throw new InvalidOperationException("Free-form SQL queries are disabled by the data access mode in AI Settings.");
            if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("sql is required");
            var trimmed = sql.Trim().TrimEnd(';').Trim();
            if (!(trimmed.StartsWith("select", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("with", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Only SELECT (or WITH ... SELECT) statements are allowed.");
            if (trimmed.Contains(";")) throw new InvalidOperationException("Multiple statements are not allowed.");
            if (ForbiddenSql.IsMatch(trimmed)) throw new InvalidOperationException("The query contains a forbidden keyword. Only read-only SELECT queries are allowed.");
            if (_settings.IsSensitive(trimmed)) throw new InvalidOperationException("The query references a sensitive keyword from AI Settings and was blocked.");

            var limit = maxRows <= 0 ? _settings.SqlQueryMaxRows : Math.Min(maxRows, _settings.SqlQueryMaxRows);
            return Execute(trimmed, limit, false);
        }

        private object Execute(string sqlText, int limit, bool reservoir)
        {
            const int ReservoirScanLimit = 5000;
            var rng = new Random();
            using (var conn = CreateConnection())
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = sqlText;
                    cmd.CommandTimeout = _settings.SqlCommandTimeoutSeconds;
                    var rows = new List<object>();
                    var cols = new List<string>();
                    var hiddenCols = new List<string>();
                    bool truncated = false;
                    int redactedValues = 0;
                    try
                    {
                        using (var r = cmd.ExecuteReader())
                        {
                            var keep = new List<int>();
                            for (int i = 0; i < r.FieldCount; i++)
                            {
                                var n = r.GetName(i);
                                if (_settings.IsSensitive(n)) hiddenCols.Add(n);
                                else { cols.Add(n); keep.Add(i); }
                            }
                            int seen = 0;
                            while (r.Read())
                            {
                                seen++;
                                int slot;
                                if (!reservoir)
                                {
                                    if (rows.Count >= limit) { truncated = true; break; }
                                    slot = -1;
                                }
                                else
                                {
                                    if (seen > ReservoirScanLimit) { truncated = true; break; }
                                    if (rows.Count < limit) slot = -1;
                                    else { slot = rng.Next(seen); if (slot >= limit) continue; }
                                }
                                var row = new object[keep.Count];
                                for (int k = 0; k < keep.Count; k++)
                                {
                                    var i = keep[k];
                                    var v = r.IsDBNull(i) ? null : r.GetValue(i);
                                    if (v is byte[]) v = "<binary " + ((byte[])v).Length + " bytes>";
                                    else if (v is DateTime) v = ((DateTime)v).ToString("s");
                                    else if (v != null && !(v is string) && !v.GetType().IsPrimitive && !(v is decimal)) v = Convert.ToString(v);
                                    if (v is string && ((string)v).Length > 500) v = ((string)v).Substring(0, 500) + "...";
                                    if (v is string && _settings.IsSensitive((string)v)) { v = "<redacted>"; redactedValues++; }
                                    row[k] = v;
                                }
                                if (slot < 0) rows.Add(row); else rows[slot] = row;
                            }
                        }
                    }
                    finally
                    {
                        try { tx.Rollback(); } catch { }
                    }
                    return new Dictionary<string, object>
                    {
                        { "columns", cols },
                        { "rows", rows },
                        { "rowCount", rows.Count },
                        { "truncated", truncated },
                        { "hiddenSensitiveColumns", hiddenCols.Count },
                        { "redactedValues", redactedValues }
                    };
                }
            }
        }

        /// <summary>ODBC data source names (user + system) visible to this 32/64-bit process.</summary>
        public static List<string> GetOdbcDsnNames()
        {
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                try
                {
                    using (var key = hive.OpenSubKey(@"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources"))
                    {
                        if (key == null) continue;
                        foreach (var n in key.GetValueNames()) if (!string.IsNullOrWhiteSpace(n)) names.Add(n);
                    }
                }
                catch { }
            }
            return names.ToList();
        }

        private string QuoteName(string name)
        {
            if (name.IndexOf('[') >= 0 || name.IndexOf('"') >= 0) return name;
            var parts = name.Split('.');
            if (IsSqlServer || string.Equals(_settings.DbProvider, "OleDb", StringComparison.OrdinalIgnoreCase))
                return string.Join(".", parts.Select(p => "[" + p.Trim() + "]"));
            return string.Join(".", parts.Select(p => "\"" + p.Trim() + "\""));
        }

        private static string FirstColumn(DataTable t, params string[] candidates)
        {
            foreach (var c in candidates) if (t.Columns.Contains(c)) return c;
            return null;
        }

        private static object SafeInt(DataRow r, string col)
        {
            if (!r.Table.Columns.Contains(col) || r[col] == DBNull.Value) return null;
            try { return Convert.ToInt32(r[col]); } catch { return null; }
        }

        private static object SafeBool(DataRow r, string col)
        {
            if (!r.Table.Columns.Contains(col) || r[col] == DBNull.Value) return null;
            try { return Convert.ToBoolean(r[col]); } catch { return null; }
        }
    }
}
