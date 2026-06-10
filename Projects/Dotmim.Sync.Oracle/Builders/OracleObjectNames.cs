using Dotmim.Sync.Builders;
using Dotmim.Sync.DatabaseStringParsers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Dotmim.Sync.Oracle.Builders
{
    /// <summary>
    /// Generates every object name and SQL/PL-SQL command text used by the Oracle provider.
    /// <para>
    /// Oracle has no table-valued parameters and cannot stream a result set out of a stored
    /// procedure the way the framework drives commands, so — like the MySQL, SQLite and
    /// PostgreSQL providers — every command is inline text. Row apply (update/delete) is an
    /// anonymous PL/SQL block that reports the affected row count through the
    /// <c>:sync_row_count</c> output bind.
    /// </para>
    /// </summary>
    public class OracleObjectNames
    {
        /// <summary>Left quote character used for Oracle identifiers.</summary>
        public const char LeftQuoteChar = '"';

        /// <summary>Right quote character used for Oracle identifiers.</summary>
        public const char RightQuoteChar = '"';

        /// <summary>
        /// Monotonic, UTC, epoch-based version expression with ~100µs resolution (mirrors the
        /// MySQL provider's <c>ROUND(UNIX_TIMESTAMP(CURRENT_TIMESTAMP(6)) * 10000)</c>).
        /// The same clock is used by the triggers, the apply-merge, UpdateUntrackedRows and
        /// <c>GetLocalTimestamp</c>. A scalar subquery is used so SYSTIMESTAMP is evaluated
        /// exactly once per reading (the seconds and fractional parts cannot straddle a
        /// second boundary).
        /// </summary>
        public const string TimestampValue =
            "(SELECT ROUND(((CAST(u AS DATE) - DATE '1970-01-01') * 86400 " +
            "+ TO_NUMBER(TO_CHAR(u, 'FF6')) / 1000000) * 10000) " +
            "FROM (SELECT SYS_EXTRACT_UTC(SYSTIMESTAMP) u FROM DUAL))";

        /// <summary>UTC "now" expression for the last_change_datetime tracking column.</summary>
        public const string NowValue = "SYS_EXTRACT_UTC(SYSTIMESTAMP)";

        private readonly SyncTable tableDescription;
        private readonly ScopeInfo scopeInfo;

        /// <summary>
        /// Initializes a new instance of the <see cref="OracleObjectNames"/> class.
        /// </summary>
        public OracleObjectNames(SyncTable tableDescription, ScopeInfo scopeInfo)
        {
            this.tableDescription = tableDescription;
            this.scopeInfo = scopeInfo;

            var tableParser = new TableParser(tableDescription.GetFullName(), LeftQuoteChar, RightQuoteChar);

            this.TableName = tableParser.TableName;
            this.TableQuotedShortName = tableParser.QuotedShortName;
            this.TableQuotedFullName = tableParser.QuotedFullName;
            this.TableSchemaName = tableParser.SchemaName;

            // Tracking table name: <prefix><table><suffix> (default suffix "_tracking"), optionally schema-qualified.
            var trackingTableNameString =
                string.IsNullOrEmpty(this.scopeInfo.Setup?.TrackingTablesPrefix) && string.IsNullOrEmpty(this.scopeInfo.Setup?.TrackingTablesSuffix)
                    ? $"{tableDescription.TableName}_tracking"
                    : $"{this.scopeInfo.Setup?.TrackingTablesPrefix}{tableDescription.TableName}{this.scopeInfo.Setup?.TrackingTablesSuffix}";

            if (!string.IsNullOrEmpty(tableDescription.SchemaName))
                trackingTableNameString = $"{tableDescription.SchemaName}.{trackingTableNameString}";

            var trackingTableParser = new TableParser(trackingTableNameString, LeftQuoteChar, RightQuoteChar);

            this.TrackingTableName = EnsureIdentifierLength(trackingTableParser.TableName);
            this.TrackingTableQuotedShortName = trackingTableParser.QuotedShortName;
            this.TrackingTableQuotedFullName = trackingTableParser.QuotedFullName;
        }

        /// <summary>Gets the left quote string.</summary>
        public string LeftQuote => "\"";

        /// <summary>Gets the right quote string.</summary>
        public string RightQuote => "\"";

        /// <summary>Gets the unquoted table name.</summary>
        public string TableName { get; }

        /// <summary>Gets the quoted short table name (without schema).</summary>
        public string TableQuotedShortName { get; }

        /// <summary>Gets the quoted full table name (with schema, if any).</summary>
        public string TableQuotedFullName { get; }

        /// <summary>Gets the parsed table schema name (may be empty).</summary>
        public string TableSchemaName { get; }

        /// <summary>Gets the unquoted tracking table name.</summary>
        public string TrackingTableName { get; }

        /// <summary>Gets the quoted short tracking table name (without schema).</summary>
        public string TrackingTableQuotedShortName { get; }

        /// <summary>Gets the quoted full tracking table name (with schema, if any).</summary>
        public string TrackingTableQuotedFullName { get; }

        /// <summary>Gets the full quoted table name (alias retained for compatibility).</summary>
        public string QuotedTableName => this.TableQuotedFullName;

        /// <summary>Gets the full quoted tracking table name (alias retained for compatibility).</summary>
        public string QuotedTrackingTableName => this.TrackingTableQuotedFullName;

        /// <summary>
        /// Oracle does not use stored procedures in this provider (everything is inline text).
        /// Returned for API compatibility only.
        /// </summary>
        public string GetStoredProcedureCommandName(DbStoredProcedureType storedProcedureType, SyncFilter filter = null) => null;

        /// <summary>
        /// Gets the (unquoted) trigger name for the given trigger type.
        /// </summary>
        public string GetTriggerCommandName(DbTriggerType triggerType)
        {
            var prefix = this.scopeInfo.Setup?.TriggersPrefix;
            var suffix = this.scopeInfo.Setup?.TriggersSuffix;
            var name = triggerType switch
            {
                DbTriggerType.Insert => $"{prefix}{this.tableDescription.TableName}{suffix}_insert_trigger",
                DbTriggerType.Update => $"{prefix}{this.tableDescription.TableName}{suffix}_update_trigger",
                DbTriggerType.Delete => $"{prefix}{this.tableDescription.TableName}{suffix}_delete_trigger",
                _ => throw new ArgumentOutOfRangeException(nameof(triggerType)),
            };

            return EnsureIdentifierLength(name);
        }

        /// <summary>
        /// Returns the inline command text for a given command type.
        /// </summary>
        public string GetCommandText(DbCommandType commandType, SyncFilter filter = null)
        {
            return commandType switch
            {
                DbCommandType.SelectChanges or DbCommandType.SelectChangesWithFilters => this.CreateSelectIncrementalChangesCommand(filter),
                DbCommandType.SelectInitializedChanges or DbCommandType.SelectInitializedChangesWithFilters => this.CreateSelectInitializedChangesCommand(filter),
                DbCommandType.SelectRow => this.CreateSelectRowCommand(),
                DbCommandType.UpdateRow or DbCommandType.InsertRow or DbCommandType.UpdateRows or DbCommandType.InsertRows => this.CreateUpdateCommand(),
                DbCommandType.DeleteRow or DbCommandType.DeleteRows => this.CreateDeleteCommand(),
                DbCommandType.DeleteMetadata => this.CreateDeleteMetadataCommand(),
                DbCommandType.Reset => this.CreateResetCommand(),
                DbCommandType.UpdateUntrackedRows => this.CreateUpdateUntrackedRowsCommand(),
                DbCommandType.DisableConstraints => this.CreateDisableConstraintsCommand(),
                DbCommandType.EnableConstraints => this.CreateEnableConstraintsCommand(),
                _ => throw new NotSupportedException($"Command type {commandType} is not supported by the Oracle provider as inline text."),
            };
        }

        // ----------------------------------------------------------------------------------------
        // Identifier / clause helpers
        // ----------------------------------------------------------------------------------------
        private static string Quoted(string columnName) => $"\"{columnName}\"";

        /// <summary>
        /// Oracle 12.2+ limits identifiers to 128 bytes. Generated names (tracking table,
        /// triggers, index, constraints) must fit; fail fast with a clear message instead
        /// of an opaque ORA-00972.
        /// </summary>
        internal static string EnsureIdentifierLength(string identifier)
        {
            if (System.Text.Encoding.UTF8.GetByteCount(identifier) > 128)
                throw new ArgumentException($"Generated Oracle identifier '{identifier}' exceeds the 128-byte limit. Use shorter table names or shorter tracking/trigger prefixes and suffixes.");

            return identifier;
        }

        private static string BindName(string columnName)
            => new ObjectParser(columnName, LeftQuoteChar, RightQuoteChar).NormalizedShortName;

        private string PrimaryKeyWhere(string tableAlias, string bindPrefix)
        {
            var and = string.Empty;
            var sb = new StringBuilder();
            foreach (var pk in this.tableDescription.GetPrimaryKeysColumns())
            {
                var alias = string.IsNullOrEmpty(tableAlias) ? string.Empty : $"{tableAlias}.";
                sb.Append($"{and}{alias}{Quoted(pk.ColumnName)} = {bindPrefix}{BindName(pk.ColumnName)}");
                and = " AND ";
            }

            return sb.ToString();
        }

        private string PrimaryKeyJoin(string leftAlias, string rightAlias)
            => string.Join(" AND ", this.tableDescription.GetPrimaryKeysColumns()
                .Select(pk => $"{leftAlias}.{Quoted(pk.ColumnName)} = {rightAlias}.{Quoted(pk.ColumnName)}"));

        // ----------------------------------------------------------------------------------------
        // Select changes / initialized changes / row
        // ----------------------------------------------------------------------------------------
        private string CreateSelectIncrementalChangesCommand(SyncFilter filter = null)
        {
            var stringBuilder = new StringBuilder(filter == null ? "SELECT " : "SELECT DISTINCT ");

            foreach (var column in this.tableDescription.GetMutableColumns(false, true))
            {
                var isPk = this.tableDescription.PrimaryKeys.Any(pk => column.ColumnName.Equals(pk, SyncGlobalization.DataSourceStringComparison));
                stringBuilder.AppendLine($"\t{(isPk ? "side" : "base")}.{Quoted(column.ColumnName)}, ");
            }

            stringBuilder.AppendLine("\tside.\"sync_row_is_tombstone\" as \"sync_row_is_tombstone\", ");
            stringBuilder.AppendLine("\tside.\"update_scope_id\" as \"sync_update_scope_id\" ");
            stringBuilder.AppendLine($"FROM {this.TableQuotedFullName} base");
            stringBuilder.Append($"RIGHT JOIN {this.TrackingTableQuotedFullName} side ON ");
            stringBuilder.AppendLine(this.PrimaryKeyJoin("base", "side"));

            if (filter != null)
                stringBuilder.Append(this.CreateFilterCustomJoins(filter));

            stringBuilder.AppendLine("WHERE (");

            if (filter != null)
                this.AppendFilterWhere(stringBuilder, filter);

            stringBuilder.AppendLine("\tside.\"timestamp\" > :sync_min_timestamp");
            stringBuilder.AppendLine("\tAND (side.\"update_scope_id\" <> :sync_scope_id OR side.\"update_scope_id\" IS NULL)");
            stringBuilder.AppendLine(")");

            return stringBuilder.ToString();
        }

        private string CreateSelectInitializedChangesCommand(SyncFilter filter = null)
        {
            var stringBuilder = new StringBuilder(filter == null ? "SELECT " : "SELECT DISTINCT ");

            var comma = "  ";
            foreach (var column in this.tableDescription.GetMutableColumns(false, true))
            {
                stringBuilder.AppendLine($"\t{comma}base.{Quoted(column.ColumnName)}");
                comma = ", ";
            }

            stringBuilder.AppendLine("\t, side.\"sync_row_is_tombstone\" as \"sync_row_is_tombstone\"");
            stringBuilder.AppendLine($"FROM {this.TableQuotedFullName} base");
            stringBuilder.Append($"LEFT JOIN {this.TrackingTableQuotedFullName} side ON ");
            stringBuilder.AppendLine(this.PrimaryKeyJoin("base", "side"));

            if (filter != null)
                stringBuilder.Append(this.CreateFilterCustomJoins(filter));

            stringBuilder.AppendLine("WHERE (");

            if (filter != null)
                this.AppendFilterWhere(stringBuilder, filter);

            stringBuilder.AppendLine("\t(side.\"timestamp\" > :sync_min_timestamp OR :sync_min_timestamp IS NULL)");
            stringBuilder.AppendLine(")");

            // Union the recent tombstones so deletions are part of the snapshot.
            stringBuilder.AppendLine("UNION");
            stringBuilder.AppendLine("SELECT ");
            comma = "  ";
            foreach (var column in this.tableDescription.GetMutableColumns(false, true))
            {
                var isPk = this.tableDescription.PrimaryKeys.Any(pk => column.ColumnName.Equals(pk, SyncGlobalization.DataSourceStringComparison));
                stringBuilder.AppendLine($"\t{comma}{(isPk ? "side" : "base")}.{Quoted(column.ColumnName)}");
                comma = ", ";
            }

            stringBuilder.AppendLine("\t, side.\"sync_row_is_tombstone\" as \"sync_row_is_tombstone\"");
            stringBuilder.AppendLine($"FROM {this.TableQuotedFullName} base");
            stringBuilder.Append($"RIGHT JOIN {this.TrackingTableQuotedFullName} side ON ");
            stringBuilder.AppendLine(this.PrimaryKeyJoin("base", "side"));
            stringBuilder.AppendLine("WHERE (side.\"timestamp\" > :sync_min_timestamp AND side.\"sync_row_is_tombstone\" = 1)");

            return stringBuilder.ToString();
        }

        private string CreateSelectRowCommand()
        {
            var stringBuilder = new StringBuilder("SELECT ");
            stringBuilder.AppendLine();

            foreach (var column in this.tableDescription.GetMutableColumns(false, true))
            {
                var isPk = this.tableDescription.PrimaryKeys.Any(pk => column.ColumnName.Equals(pk, SyncGlobalization.DataSourceStringComparison));
                stringBuilder.AppendLine($"\t{(isPk ? "side" : "base")}.{Quoted(column.ColumnName)}, ");
            }

            stringBuilder.AppendLine("\tside.\"sync_row_is_tombstone\" as \"sync_row_is_tombstone\", ");
            stringBuilder.AppendLine("\tside.\"update_scope_id\" as \"sync_update_scope_id\"");
            stringBuilder.AppendLine($"FROM {this.TableQuotedFullName} base");
            stringBuilder.Append($"RIGHT JOIN {this.TrackingTableQuotedFullName} side ON ");
            stringBuilder.AppendLine(this.PrimaryKeyJoin("base", "side"));
            stringBuilder.Append("WHERE ");
            stringBuilder.Append(this.PrimaryKeyWhere("side", ":"));

            return stringBuilder.ToString();
        }

        // ----------------------------------------------------------------------------------------
        // Apply row : update (upsert) / delete as anonymous PL/SQL blocks
        // ----------------------------------------------------------------------------------------
        private string CreateUpdateCommand()
        {
            var writableColumns = this.tableDescription.Columns.Where(c => !c.IsReadOnly).ToList();
            var mutableColumns = this.tableDescription.GetMutableColumns(false, false).ToList();
            var hasMutableColumns = mutableColumns.Count > 0;

            const string guard = "(v_ts IS NULL OR v_ts <= :sync_min_timestamp OR v_scope = :sync_scope_id OR :sync_force_write = 1)";

            var sb = new StringBuilder();
            sb.AppendLine("DECLARE");
            sb.AppendLine("  v_ts NUMBER;");
            sb.AppendLine("  v_scope RAW(16);");
            sb.AppendLine("  v_count NUMBER := 0;");
            sb.AppendLine("BEGIN");
            sb.AppendLine("  BEGIN");
            sb.AppendLine($"    SELECT \"timestamp\", \"update_scope_id\" INTO v_ts, v_scope");
            sb.AppendLine($"    FROM {this.TrackingTableQuotedFullName}");
            sb.AppendLine($"    WHERE {this.PrimaryKeyWhere(string.Empty, ":")} AND ROWNUM = 1;");
            sb.AppendLine("  EXCEPTION WHEN NO_DATA_FOUND THEN v_ts := NULL; v_scope := NULL; END;");
            sb.AppendLine();

            if (hasMutableColumns)
            {
                var setClause = string.Join(", ", mutableColumns.Select(c => $"{Quoted(c.ColumnName)} = :{BindName(c.ColumnName)}"));
                sb.AppendLine($"  UPDATE {this.TableQuotedFullName} SET {setClause}");
                sb.AppendLine($"  WHERE {this.PrimaryKeyWhere(string.Empty, ":")} AND {guard};");
                sb.AppendLine("  v_count := SQL%ROWCOUNT;");
                sb.AppendLine();
                sb.AppendLine("  IF v_count = 0 THEN");
                this.AppendGuardedInsert(sb, writableColumns, guard, "    ");
                sb.AppendLine("  END IF;");
            }
            else
            {
                this.AppendGuardedInsert(sb, writableColumns, guard, "  ");
            }

            sb.AppendLine();
            sb.AppendLine("  IF v_count > 0 THEN");
            this.AppendTrackingMerge(sb, tombstone: 0, scopeBind: ":sync_scope_id", indent: "    ");
            sb.AppendLine("  END IF;");
            sb.AppendLine("  :sync_row_count := v_count;");
            sb.AppendLine("END;");

            return sb.ToString();
        }

        private string CreateDeleteCommand()
        {
            const string guard = "(v_ts IS NULL OR v_ts <= :sync_min_timestamp OR v_scope = :sync_scope_id OR :sync_force_write = 1)";

            var sb = new StringBuilder();
            sb.AppendLine("DECLARE");
            sb.AppendLine("  v_ts NUMBER;");
            sb.AppendLine("  v_scope RAW(16);");
            sb.AppendLine("  v_count NUMBER := 0;");
            sb.AppendLine("BEGIN");
            sb.AppendLine("  BEGIN");
            sb.AppendLine($"    SELECT \"timestamp\", \"update_scope_id\" INTO v_ts, v_scope");
            sb.AppendLine($"    FROM {this.TrackingTableQuotedFullName}");
            sb.AppendLine($"    WHERE {this.PrimaryKeyWhere(string.Empty, ":")} AND ROWNUM = 1;");
            sb.AppendLine("  EXCEPTION WHEN NO_DATA_FOUND THEN v_ts := NULL; v_scope := NULL; END;");
            sb.AppendLine();
            sb.AppendLine($"  DELETE FROM {this.TableQuotedFullName}");
            sb.AppendLine($"  WHERE {this.PrimaryKeyWhere(string.Empty, ":")} AND {guard};");
            sb.AppendLine("  v_count := SQL%ROWCOUNT;");
            sb.AppendLine();
            sb.AppendLine("  IF v_count > 0 THEN");
            this.AppendTrackingMerge(sb, tombstone: 1, scopeBind: ":sync_scope_id", indent: "    ");
            sb.AppendLine("  END IF;");
            sb.AppendLine("  :sync_row_count := v_count;");
            sb.AppendLine("END;");

            return sb.ToString();
        }

        private void AppendGuardedInsert(StringBuilder sb, IReadOnlyList<SyncColumn> writableColumns, string guard, string indent)
        {
            var columnList = string.Join(", ", writableColumns.Select(c => Quoted(c.ColumnName)));
            var valueList = string.Join(", ", writableColumns.Select(c => $":{BindName(c.ColumnName)}"));

            sb.AppendLine($"{indent}BEGIN");
            sb.AppendLine($"{indent}  INSERT INTO {this.TableQuotedFullName} ({columnList})");
            sb.AppendLine($"{indent}  SELECT {valueList} FROM DUAL WHERE {guard};");
            sb.AppendLine($"{indent}  v_count := SQL%ROWCOUNT;");
            sb.AppendLine($"{indent}EXCEPTION WHEN DUP_VAL_ON_INDEX THEN v_count := 0; END;");
        }

        private void AppendTrackingMerge(StringBuilder sb, int tombstone, string scopeBind, string indent)
        {
            var pkColumns = this.tableDescription.GetPrimaryKeysColumns().ToList();
            var pkList = string.Join(", ", pkColumns.Select(c => Quoted(c.ColumnName)));
            var pkValues = string.Join(", ", pkColumns.Select(c => $":{BindName(c.ColumnName)}"));

            sb.AppendLine($"{indent}MERGE INTO {this.TrackingTableQuotedFullName} t USING DUAL ON ({this.PrimaryKeyWhere("t", ":")})");
            sb.AppendLine($"{indent}WHEN MATCHED THEN UPDATE SET");
            sb.AppendLine($"{indent}  t.\"update_scope_id\" = {scopeBind},");
            sb.AppendLine($"{indent}  t.\"sync_row_is_tombstone\" = {tombstone},");
            sb.AppendLine($"{indent}  t.\"timestamp\" = {TimestampValue},");
            sb.AppendLine($"{indent}  t.\"last_change_datetime\" = {NowValue}");
            sb.AppendLine($"{indent}WHEN NOT MATCHED THEN INSERT ({pkList}, \"update_scope_id\", \"sync_row_is_tombstone\", \"timestamp\", \"last_change_datetime\")");
            sb.AppendLine($"{indent}  VALUES ({pkValues}, {scopeBind}, {tombstone}, {TimestampValue}, {NowValue});");
        }

        // ----------------------------------------------------------------------------------------
        // Metadata / reset / untracked / constraints
        // ----------------------------------------------------------------------------------------
        private string CreateDeleteMetadataCommand()
            => $"DELETE FROM {this.TrackingTableQuotedFullName} WHERE \"timestamp\" <= :sync_row_timestamp";

        private string CreateResetCommand()
        {
            var sb = new StringBuilder();
            sb.AppendLine("BEGIN");
            sb.AppendLine($"  DELETE FROM {this.TableQuotedFullName};");
            sb.AppendLine($"  DELETE FROM {this.TrackingTableQuotedFullName};");
            sb.AppendLine("  :sync_row_count := SQL%ROWCOUNT;");
            sb.AppendLine("END;");
            return sb.ToString();
        }

        private string CreateUpdateUntrackedRowsCommand()
        {
            var pkColumns = this.tableDescription.GetPrimaryKeysColumns().ToList();
            var pkList = string.Join(", ", pkColumns.Select(c => Quoted(c.ColumnName)));
            var pkSelect = string.Join(", ", pkColumns.Select(c => $"base.{Quoted(c.ColumnName)}"));

            var sb = new StringBuilder();
            sb.AppendLine($"INSERT INTO {this.TrackingTableQuotedFullName} ({pkList}, \"update_scope_id\", \"sync_row_is_tombstone\", \"timestamp\", \"last_change_datetime\")");
            sb.AppendLine($"SELECT {pkSelect}, NULL, 0, {TimestampValue}, {NowValue}");
            sb.AppendLine($"FROM {this.TableQuotedFullName} base");
            sb.AppendLine($"LEFT JOIN {this.TrackingTableQuotedFullName} side ON {this.PrimaryKeyJoin("base", "side")}");
            sb.AppendLine($"WHERE side.{Quoted(pkColumns[0].ColumnName)} IS NULL");
            return sb.ToString();
        }

        private string CreateDisableConstraintsCommand()
        {
            var tableName = this.TableName.Replace("'", "''");
            var sb = new StringBuilder();
            sb.AppendLine("BEGIN");
            sb.AppendLine("  FOR c IN (SELECT table_name, constraint_name FROM user_constraints");
            sb.AppendLine($"            WHERE r_constraint_name IN (SELECT constraint_name FROM user_constraints WHERE table_name = '{tableName}')");
            sb.AppendLine("            AND constraint_type = 'R') LOOP");
            sb.AppendLine("    EXECUTE IMMEDIATE 'ALTER TABLE \"' || c.table_name || '\" DISABLE CONSTRAINT \"' || c.constraint_name || '\"';");
            sb.AppendLine("  END LOOP;");
            sb.AppendLine($"  FOR c IN (SELECT constraint_name FROM user_constraints WHERE table_name = '{tableName}' AND constraint_type = 'R') LOOP");
            sb.AppendLine($"    EXECUTE IMMEDIATE 'ALTER TABLE {this.TableQuotedFullName} DISABLE CONSTRAINT \"' || c.constraint_name || '\"';");
            sb.AppendLine("  END LOOP;");
            sb.AppendLine("END;");
            return sb.ToString();
        }

        // Oracle's bare ENABLE CONSTRAINT means ENABLE VALIDATE, which rescans all rows and
        // fails on orphans mid-sync; SqlServer re-enables with NOCHECK semantics — NOVALIDATE
        // is the parity behavior.
        private string CreateEnableConstraintsCommand()
        {
            var tableName = this.TableName.Replace("'", "''");
            var sb = new StringBuilder();
            sb.AppendLine("BEGIN");
            sb.AppendLine($"  FOR c IN (SELECT constraint_name FROM user_constraints WHERE table_name = '{tableName}' AND constraint_type = 'R') LOOP");
            sb.AppendLine($"    EXECUTE IMMEDIATE 'ALTER TABLE {this.TableQuotedFullName} ENABLE NOVALIDATE CONSTRAINT \"' || c.constraint_name || '\"';");
            sb.AppendLine("  END LOOP;");
            sb.AppendLine("  FOR c IN (SELECT table_name, constraint_name FROM user_constraints");
            sb.AppendLine($"            WHERE r_constraint_name IN (SELECT constraint_name FROM user_constraints WHERE table_name = '{tableName}')");
            sb.AppendLine("            AND constraint_type = 'R') LOOP");
            sb.AppendLine("    EXECUTE IMMEDIATE 'ALTER TABLE \"' || c.table_name || '\" ENABLE NOVALIDATE CONSTRAINT \"' || c.constraint_name || '\"';");
            sb.AppendLine("  END LOOP;");
            sb.AppendLine("END;");
            return sb.ToString();
        }

        // ----------------------------------------------------------------------------------------
        // Tracking table & triggers DDL
        // ----------------------------------------------------------------------------------------

        /// <summary>
        /// Builds a PL/SQL block creating the tracking table (primary keys + sync metadata
        /// columns) and its timestamp index. Two EXECUTE IMMEDIATE calls because Oracle
        /// cannot batch two DDL statements in a single command.
        /// </summary>
        public string CreateTrackingTableScript(Func<SyncColumn, string> columnTypeResolver)
        {
            var createTable = new StringBuilder();
            createTable.AppendLine($"CREATE TABLE {this.TrackingTableQuotedFullName} (");

            foreach (var pk in this.tableDescription.GetPrimaryKeysColumns())
                createTable.AppendLine($"  {Quoted(pk.ColumnName)} {columnTypeResolver(pk)} NOT NULL,");

            createTable.AppendLine("  \"update_scope_id\" RAW(16) NULL,");
            createTable.AppendLine("  \"timestamp\" NUMBER(19) NULL,");
            createTable.AppendLine("  \"sync_row_is_tombstone\" NUMBER(1) DEFAULT 0 NOT NULL,");
            createTable.AppendLine("  \"last_change_datetime\" TIMESTAMP NULL,");

            var pkList = string.Join(", ", this.tableDescription.GetPrimaryKeysColumns().Select(c => Quoted(c.ColumnName)));
            createTable.AppendLine($"  CONSTRAINT {Quoted(EnsureIdentifierLength($"PK_{this.TrackingTableName}"))} PRIMARY KEY ({pkList})");
            createTable.Append(')');

            var indexName = EnsureIdentifierLength($"{this.TrackingTableName}_ts_idx");
            var createIndex = $"CREATE INDEX {Quoted(indexName)} ON {this.TrackingTableQuotedFullName} (\"timestamp\")";

            var sb = new StringBuilder();
            sb.AppendLine("BEGIN");
            sb.AppendLine($"  EXECUTE IMMEDIATE '{createTable.ToString().Replace("'", "''")}';");
            sb.AppendLine($"  EXECUTE IMMEDIATE '{createIndex.Replace("'", "''")}';");
            sb.AppendLine("END;");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the CREATE OR REPLACE TRIGGER script for the given trigger type.
        /// </summary>
        public string CreateTriggerScript(DbTriggerType triggerType)
        {
            var triggerName = this.GetTriggerCommandName(triggerType);
            var pkColumns = this.tableDescription.GetPrimaryKeysColumns().ToList();
            var newOrOld = triggerType == DbTriggerType.Delete ? ":OLD" : ":NEW";
            var tombstone = triggerType == DbTriggerType.Delete ? 1 : 0;
            var (action, _) = triggerType switch
            {
                DbTriggerType.Insert => ("AFTER INSERT", 0),
                DbTriggerType.Update => ("AFTER UPDATE", 0),
                DbTriggerType.Delete => ("AFTER DELETE", 1),
                _ => throw new ArgumentOutOfRangeException(nameof(triggerType)),
            };

            var onClause = string.Join(" AND ", pkColumns.Select(c => $"t.{Quoted(c.ColumnName)} = {newOrOld}.{Quoted(c.ColumnName)}"));
            var pkList = string.Join(", ", pkColumns.Select(c => Quoted(c.ColumnName)));
            var pkValues = string.Join(", ", pkColumns.Select(c => $"{newOrOld}.{Quoted(c.ColumnName)}"));

            var sb = new StringBuilder();
            sb.AppendLine($"CREATE OR REPLACE TRIGGER {Quoted(triggerName)}");
            sb.AppendLine($"{action} ON {this.TableQuotedFullName}");
            sb.AppendLine("FOR EACH ROW");
            sb.AppendLine("BEGIN");
            sb.AppendLine($"  MERGE INTO {this.TrackingTableQuotedFullName} t USING DUAL ON ({onClause})");
            sb.AppendLine("  WHEN MATCHED THEN UPDATE SET");
            sb.AppendLine("    t.\"update_scope_id\" = NULL,");
            sb.AppendLine($"    t.\"sync_row_is_tombstone\" = {tombstone},");
            sb.AppendLine($"    t.\"timestamp\" = {TimestampValue},");
            sb.AppendLine($"    t.\"last_change_datetime\" = {NowValue}");
            sb.AppendLine($"  WHEN NOT MATCHED THEN INSERT ({pkList}, \"update_scope_id\", \"sync_row_is_tombstone\", \"timestamp\", \"last_change_datetime\")");
            sb.AppendLine($"    VALUES ({pkValues}, NULL, {tombstone}, {TimestampValue}, {NowValue});");
            sb.AppendLine("END;");

            return sb.ToString();
        }

        // ----------------------------------------------------------------------------------------
        // Filters
        // ----------------------------------------------------------------------------------------
        private void AppendFilterWhere(StringBuilder stringBuilder, SyncFilter filter)
        {
            var whereSide = this.CreateFilterWhereSide(filter);
            if (!string.IsNullOrEmpty(whereSide))
            {
                stringBuilder.Append(whereSide);
                stringBuilder.AppendLine("\tAND ");
            }

            var customWheres = this.CreateFilterCustomWheres(filter);
            if (!string.IsNullOrEmpty(customWheres))
            {
                stringBuilder.Append(customWheres);
                stringBuilder.AppendLine("\tAND ");
            }
        }

        private string CreateFilterWhereSide(SyncFilter filter)
        {
            var sideWhereFilters = filter.Wheres;
            if (sideWhereFilters.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("\t((");
            var and = "   ";

            foreach (var whereFilter in sideWhereFilters)
            {
                var tableFilter = this.tableDescription.Schema.Tables[whereFilter.TableName, whereFilter.SchemaName]
                    ?? throw new FilterParamTableNotExistsException(whereFilter.TableName);

                var columnFilter = tableFilter.Columns[whereFilter.ColumnName]
                    ?? throw new FilterParamColumnNotExistsException(whereFilter.ColumnName, whereFilter.TableName);

                var tableName = string.Equals(tableFilter.TableName, filter.TableName, SyncGlobalization.DataSourceStringComparison)
                    ? "base"
                    : $"\"{tableFilter.TableName}\"";

                var parameterName = BindName(whereFilter.ParameterName);
                var param = filter.Parameters[parameterName];
                if (param == null)
                    throw new FilterParamColumnNotExistsException(whereFilter.ColumnName, whereFilter.TableName);

                sb.Append($"{and}({tableName}.{Quoted(columnFilter.ColumnName)} = :{parameterName}");
                if (param.AllowNull)
                    sb.Append($" OR :{parameterName} IS NULL");
                sb.Append(")");
                and = " AND ";
            }

            sb.AppendLine();
            sb.AppendLine("\t)");
            sb.AppendLine("\tOR side.\"sync_row_is_tombstone\" = 1");
            sb.AppendLine("\t)");
            return sb.ToString();
        }

        private string CreateFilterCustomWheres(SyncFilter filter)
        {
            var customWheres = filter.CustomWheres;
            if (customWheres.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            var and = "  ";
            sb.AppendLine("\t(");
            foreach (var customWhere in customWheres)
            {
                var iteration = customWhere.Replace("{{{", "\"").Replace("}}}", "\"");
                sb.Append($"{and}{iteration}");
                and = " AND ";
            }

            sb.AppendLine();
            sb.AppendLine("\t)");
            return sb.ToString();
        }

        /// <summary>
        /// Builds the custom JOIN clauses declared on the filter (ported from the MySQL
        /// provider). The filter table itself is aliased as <c>base</c>; <see cref="Join.Outer"/>
        /// maps to FULL OUTER JOIN (Oracle has no bare OUTER JOIN).
        /// </summary>
        public string CreateFilterCustomJoins(SyncFilter filter)
        {
            var customJoins = filter.Joins;

            if (customJoins.Count == 0)
                return string.Empty;

            var stringBuilder = new StringBuilder();
            stringBuilder.AppendLine();

            foreach (var customJoin in customJoins)
            {
                switch (customJoin.JoinEnum)
                {
                    case Join.Left:
                        stringBuilder.Append("LEFT JOIN ");
                        break;
                    case Join.Right:
                        stringBuilder.Append("RIGHT JOIN ");
                        break;
                    case Join.Outer:
                        stringBuilder.Append("FULL OUTER JOIN ");
                        break;
                    case Join.Inner:
                    default:
                        stringBuilder.Append("INNER JOIN ");
                        break;
                }

                var filterTableParser = new TableParser(filter.TableName, LeftQuoteChar, RightQuoteChar);
                var joinTableParser = new TableParser(customJoin.TableName, LeftQuoteChar, RightQuoteChar);

                var leftTableParser = new TableParser(customJoin.LeftTableName, LeftQuoteChar, RightQuoteChar);
                var leftTableName = leftTableParser.QuotedShortName;
                if (string.Equals(filterTableParser.QuotedShortName, leftTableName, SyncGlobalization.DataSourceStringComparison))
                    leftTableName = "base";

                var rightTableParser = new TableParser(customJoin.RightTableName, LeftQuoteChar, RightQuoteChar);
                var rightTableName = rightTableParser.QuotedShortName;
                if (string.Equals(filterTableParser.QuotedShortName, rightTableName, SyncGlobalization.DataSourceStringComparison))
                    rightTableName = "base";

                var leftColumnParser = new ObjectParser(customJoin.LeftColumnName, LeftQuoteChar, RightQuoteChar);
                var rightColumnParser = new ObjectParser(customJoin.RightColumnName, LeftQuoteChar, RightQuoteChar);

                stringBuilder.AppendLine($"{joinTableParser.QuotedShortName} ON {leftTableName}.{leftColumnParser.QuotedShortName} = {rightTableName}.{rightColumnParser.QuotedShortName}");
            }

            return stringBuilder.ToString();
        }
    }
}
