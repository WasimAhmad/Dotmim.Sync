using Dotmim.Sync.Builders;
using Dotmim.Sync.Oracle.Manager;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Manager;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dotmim.Sync.DatabaseStringParsers;

namespace Dotmim.Sync.Oracle.Builders
{
    public class OracleTableBuilder : DbTableBuilder
    {
        private readonly OracleObjectNames oracleObjectNames;
        private readonly OracleDbMetadata oracleDbMetadata;
        private readonly DbTableNames tableNames;
        private readonly DbTableNames trackingTableNames;

        public OracleTableBuilder(SyncTable tableDescription, ScopeInfo scopeInfo) : base(tableDescription, scopeInfo)
        {
            this.oracleObjectNames = new OracleObjectNames(tableDescription, scopeInfo);
            this.oracleDbMetadata = new OracleDbMetadata();
            
            // Create parsers using the correct char parameters
            char leftQuote = this.oracleObjectNames.LeftQuote[0];
            char rightQuote = this.oracleObjectNames.RightQuote[0];
            
            // Parse table name
            var tableParser = new ObjectParser(tableDescription.TableName, leftQuote, rightQuote);
            string schema = tableDescription.SchemaName ?? "";
            
            // Initialize tableNames with all required properties
            this.tableNames = new DbTableNames(leftQuote, rightQuote,
                tableDescription.TableName, // name
                schema.Length > 0 ? $"{schema}.{tableDescription.TableName}" : tableDescription.TableName, // normalizedFullName
                tableParser.NormalizedShortName, // normalizedName
                schema.Length > 0 ? $"{schema}.{tableParser.QuotedShortName}" : tableParser.QuotedShortName, // quotedFullName
                tableParser.QuotedShortName, // quotedName
                schema); // schemaName

            // Parse tracking table name
            var trackingTableParser = new ObjectParser(this.oracleObjectNames.TrackingTableName, leftQuote, rightQuote);
            
            // Initialize trackingTableNames with all required properties
            this.trackingTableNames = new DbTableNames(leftQuote, rightQuote,
                this.oracleObjectNames.TrackingTableName, // name
                schema.Length > 0 ? $"{schema}.{this.oracleObjectNames.TrackingTableName}" : this.oracleObjectNames.TrackingTableName, // normalizedFullName
                trackingTableParser.NormalizedShortName, // normalizedName
                schema.Length > 0 ? $"{schema}.{trackingTableParser.QuotedShortName}" : trackingTableParser.QuotedShortName, // quotedFullName
                trackingTableParser.QuotedShortName, // quotedName
                schema); // schemaName
        }

        private async Task<bool> InternalExistsTableAsync(DbConnection connection, DbTransaction transaction = null)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM USER_TABLES 
                WHERE TABLE_NAME = :tableName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.TableDescription.TableName.ToUpper(); // Oracle stores identifiers in upper case by default
            command.Parameters.Add(parameter);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    connection.Open();

                var scalarResult = await command.ExecuteScalarAsync();
                int count = Convert.ToInt32(scalarResult);
                return count > 0;
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    connection.Close();
            }
        }

        private async Task<bool> InternalExistsTrackingTableAsync(DbConnection connection, DbTransaction transaction = null)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM USER_TABLES 
                WHERE TABLE_NAME = :tableName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.oracleObjectNames.TrackingTableName.ToUpper(); // Oracle stores identifiers in upper case by default
            command.Parameters.Add(parameter);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    connection.Open();

                var scalarResult = await command.ExecuteScalarAsync();
                int count = Convert.ToInt32(scalarResult);
                return count > 0;
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    connection.Close();
            }
        }

        private string InternalCreateTableScriptText()
        {
            var stringBuilder = new StringBuilder();

            stringBuilder.AppendLine($"CREATE TABLE {this.oracleObjectNames.QuotedTableName} (");

            string comma = string.Empty;
            foreach (var column in this.TableDescription.Columns)
            {
                var columnTypeString = this.GetOracleColumnTypeString(column);
                var nullScriptString = column.AllowDBNull ? "NULL" : "NOT NULL";
                
                stringBuilder.AppendLine($"{comma}  \"{column.ColumnName}\" {columnTypeString} {nullScriptString}");
                comma = ",";
            }

            string pkColumnsString = string.Join(", ", this.TableDescription.GetPrimaryKeysColumns()
                .Select(c => $"\"{c.ColumnName}\""));

            // Add primary key constraint
            stringBuilder.AppendLine($"{comma}  CONSTRAINT \"PK_{this.TableDescription.TableName}\" PRIMARY KEY ({pkColumnsString})");
            stringBuilder.AppendLine(")");

            return stringBuilder.ToString();
        }

        private string InternalCreateTrackingTableScriptText()
        {
            var stringBuilder = new StringBuilder();

            stringBuilder.AppendLine($"CREATE TABLE {this.oracleObjectNames.QuotedTrackingTableName} (");

            string comma = string.Empty;
            foreach (var pkColumn in this.TableDescription.GetPrimaryKeysColumns())
            {
                var columnTypeString = this.GetOracleColumnTypeString(pkColumn);
                stringBuilder.AppendLine($"{comma}  \"{pkColumn.ColumnName}\" {columnTypeString} NOT NULL");
                comma = ",";
            }

            // Add tracking columns
            stringBuilder.AppendLine($"{comma}  \"update_scope_id\" VARCHAR2(36) NULL");
            stringBuilder.AppendLine($",  \"sync_row_is_tombstone\" NUMBER(1) NOT NULL");
            stringBuilder.AppendLine($",  \"update_timestamp\" NUMBER NOT NULL");
            stringBuilder.AppendLine($",  \"last_change_datetime\" TIMESTAMP NOT NULL");

            // Add primary key constraint for tracking table
            string pkColumnsString = string.Join(", ", this.TableDescription.GetPrimaryKeysColumns()
                .Select(c => $"\"{c.ColumnName}\""));

            stringBuilder.AppendLine($",  CONSTRAINT \"PK_{this.oracleObjectNames.TrackingTableName}\" PRIMARY KEY ({pkColumnsString})");
            stringBuilder.AppendLine(")");

            return stringBuilder.ToString();
        }

        private string InternalCreateTriggerScriptText(DbTriggerType triggerType)
        {
            var stringBuilder = new StringBuilder();
            var triggerName = this.oracleObjectNames.GetTriggerCommandName(triggerType);
            var tableName = this.oracleObjectNames.QuotedTableName;
            var trackingTableName = this.oracleObjectNames.QuotedTrackingTableName;

            // Get primary key columns
            var pkColumns = this.TableDescription.GetPrimaryKeysColumns();
            var pkColumnsList = string.Join(", ", pkColumns.Select(c => $"\"{c.ColumnName}\""));

            string pkWhereCondition = string.Join(" AND ", pkColumns.Select(c => 
                $"t.\"{c.ColumnName}\" = {(triggerType == DbTriggerType.Delete ? "OLD" : "NEW")}.\"{c.ColumnName}\""));

            switch (triggerType)
            {
                case DbTriggerType.Insert:
                    stringBuilder.AppendLine($"CREATE OR REPLACE TRIGGER {triggerName}");
                    stringBuilder.AppendLine($"AFTER INSERT ON {tableName}");
                    stringBuilder.AppendLine($"FOR EACH ROW");
                    stringBuilder.AppendLine($"BEGIN");
                    stringBuilder.AppendLine($"  MERGE INTO {trackingTableName} t");
                    stringBuilder.AppendLine($"  USING DUAL");
                    stringBuilder.AppendLine($"  ON ({pkWhereCondition})");
                    stringBuilder.AppendLine($"  WHEN MATCHED THEN");
                    stringBuilder.AppendLine($"    UPDATE SET");
                    stringBuilder.AppendLine($"      t.\"update_scope_id\" = NULL,");
                    stringBuilder.AppendLine($"      t.\"sync_row_is_tombstone\" = 0,");
                    stringBuilder.AppendLine($"      t.\"update_timestamp\" = (SELECT MAX(\"update_timestamp\") + 1 FROM {trackingTableName}),");
                    stringBuilder.AppendLine($"      t.\"last_change_datetime\" = SYSDATE");
                    stringBuilder.AppendLine($"  WHEN NOT MATCHED THEN");
                    stringBuilder.AppendLine($"    INSERT ({string.Join(", ", pkColumns.Select(c => $"\"{c.ColumnName}\""))}, \"update_scope_id\", \"sync_row_is_tombstone\", \"update_timestamp\", \"last_change_datetime\")");
                    stringBuilder.AppendLine($"    VALUES ({string.Join(", ", pkColumns.Select(c => $"NEW.\"{c.ColumnName}\""))}, NULL, 0, COALESCE((SELECT MAX(\"update_timestamp\") FROM {trackingTableName}), 0) + 1, SYSDATE);");
                    stringBuilder.AppendLine($"END;");
                    break;
                case DbTriggerType.Update:
                    stringBuilder.AppendLine($"CREATE OR REPLACE TRIGGER {triggerName}");
                    stringBuilder.AppendLine($"AFTER UPDATE ON {tableName}");
                    stringBuilder.AppendLine($"FOR EACH ROW");
                    stringBuilder.AppendLine($"BEGIN");
                    stringBuilder.AppendLine($"  MERGE INTO {trackingTableName} t");
                    stringBuilder.AppendLine($"  USING DUAL");
                    stringBuilder.AppendLine($"  ON ({pkWhereCondition})");
                    stringBuilder.AppendLine($"  WHEN MATCHED THEN");
                    stringBuilder.AppendLine($"    UPDATE SET");
                    stringBuilder.AppendLine($"      t.\"update_scope_id\" = NULL,");
                    stringBuilder.AppendLine($"      t.\"sync_row_is_tombstone\" = 0,");
                    stringBuilder.AppendLine($"      t.\"update_timestamp\" = (SELECT MAX(\"update_timestamp\") + 1 FROM {trackingTableName}),");
                    stringBuilder.AppendLine($"      t.\"last_change_datetime\" = SYSDATE");
                    stringBuilder.AppendLine($"  WHEN NOT MATCHED THEN");
                    stringBuilder.AppendLine($"    INSERT ({string.Join(", ", pkColumns.Select(c => $"\"{c.ColumnName}\""))}, \"update_scope_id\", \"sync_row_is_tombstone\", \"update_timestamp\", \"last_change_datetime\")");
                    stringBuilder.AppendLine($"    VALUES ({string.Join(", ", pkColumns.Select(c => $"NEW.\"{c.ColumnName}\""))}, NULL, 0, COALESCE((SELECT MAX(\"update_timestamp\") FROM {trackingTableName}), 0) + 1, SYSDATE);");
                    stringBuilder.AppendLine($"END;");
                    break;
                case DbTriggerType.Delete:
                    stringBuilder.AppendLine($"CREATE OR REPLACE TRIGGER {triggerName}");
                    stringBuilder.AppendLine($"AFTER DELETE ON {tableName}");
                    stringBuilder.AppendLine($"FOR EACH ROW");
                    stringBuilder.AppendLine($"BEGIN");
                    stringBuilder.AppendLine($"  MERGE INTO {trackingTableName} t");
                    stringBuilder.AppendLine($"  USING DUAL");
                    stringBuilder.AppendLine($"  ON ({pkWhereCondition})");
                    stringBuilder.AppendLine($"  WHEN MATCHED THEN");
                    stringBuilder.AppendLine($"    UPDATE SET");
                    stringBuilder.AppendLine($"      t.\"update_scope_id\" = NULL,");
                    stringBuilder.AppendLine($"      t.\"sync_row_is_tombstone\" = 1,");
                    stringBuilder.AppendLine($"      t.\"update_timestamp\" = (SELECT MAX(\"update_timestamp\") + 1 FROM {trackingTableName}),");
                    stringBuilder.AppendLine($"      t.\"last_change_datetime\" = SYSDATE");
                    stringBuilder.AppendLine($"  WHEN NOT MATCHED THEN");
                    stringBuilder.AppendLine($"    INSERT ({string.Join(", ", pkColumns.Select(c => $"\"{c.ColumnName}\""))}, \"update_scope_id\", \"sync_row_is_tombstone\", \"update_timestamp\", \"last_change_datetime\")");
                    stringBuilder.AppendLine($"    VALUES ({string.Join(", ", pkColumns.Select(c => $"OLD.\"{c.ColumnName}\""))}, NULL, 1, COALESCE((SELECT MAX(\"update_timestamp\") FROM {trackingTableName}), 0) + 1, SYSDATE);");
                    stringBuilder.AppendLine($"END;");
                    break;
            }

            return stringBuilder.ToString();
        }

        private string GetOracleColumnTypeString(SyncColumn column)
        {
            string typeName = column.OriginalTypeName;
            if (string.IsNullOrEmpty(typeName))
                typeName = column.GetDbType().ToString();

            // Oracle type conversion
            string upperTypeName = typeName.ToUpperInvariant();
            
            // Handle special conversions for Oracle types
            switch (upperTypeName)
            {
                case "BIT":
                case "BOOLEAN":
                    return "NUMBER(1)";
                case "TINYINT":
                case "SMALLINT":
                    return "NUMBER(5)";
                case "INT":
                case "INTEGER":
                    return "NUMBER(10)";
                case "BIGINT":
                    return "NUMBER(19)";
                case "DECIMAL":
                case "NUMERIC":
                    return column.Precision > 0 && column.Scale >= 0 
                        ? $"NUMBER({column.Precision},{column.Scale})" 
                        : "NUMBER";
                case "FLOAT":
                case "REAL":
                    return "BINARY_FLOAT";
                case "DOUBLE":
                    return "BINARY_DOUBLE";
                case "DATETIME":
                case "DATETIME2":
                case "SMALLDATETIME":
                    return "TIMESTAMP";
                case "DATETIMEOFFSET":
                    return "TIMESTAMP WITH TIME ZONE";
                case "DATE":
                    return "DATE";
                case "TIME":
                    return "TIMESTAMP";
                case "CHAR":
                case "NCHAR":
                    return column.MaxLength > 0 
                        ? $"CHAR({Math.Min(column.MaxLength, 2000)})" 
                        : "CHAR(1)";
                case "VARCHAR":
                case "NVARCHAR":
                    return column.MaxLength > 0 && column.MaxLength <= 4000
                        ? $"VARCHAR2({column.MaxLength})"
                        : "VARCHAR2(4000)";
                case "TEXT":
                case "NTEXT":
                    return "CLOB";
                case "BINARY":
                case "VARBINARY":
                    return column.MaxLength > 0 && column.MaxLength <= 2000
                        ? $"RAW({column.MaxLength})" 
                        : "BLOB";
                case "IMAGE":
                    return "BLOB";
                case "UNIQUEIDENTIFIER":
                    return "VARCHAR2(36)";
                case "XML":
                    return "XMLTYPE";
                case "GEOGRAPHY":
                case "GEOMETRY":
                    // Oracle Spatial is not directly compatible; this is a simplification
                    return "BLOB";
                default:
                    // For any unknown type, default to VARCHAR2
                    return "VARCHAR2(4000)";
            }
        }

        // Override the abstract method from DbTableBuilder
        public override DbTableNames GetParsedTableNames()
        {
            return this.tableNames;
        }

        // Override the abstract method from DbTableBuilder
        public override DbTableNames GetParsedTrackingTableNames()
        {
            return this.trackingTableNames;
        }

        // Override the abstract method from DbTableBuilder
        public override DbColumnNames GetParsedColumnNames(SyncColumn column)
        {
            var columnParser = new ObjectParser(column.ColumnName, oracleObjectNames.LeftQuote[0], oracleObjectNames.RightQuote[0]);
            return new DbColumnNames(columnParser.QuotedShortName, columnParser.NormalizedShortName);
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetCreateSchemaCommandAsync(DbConnection connection, DbTransaction transaction)
        {
            // In Oracle, USER_SCHEMAS are not created like in SQL Server
            // Usually, schemas are created by creating a user
            // For now, we'll just return a dummy command since schema creation is handled differently in Oracle
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM DUAL"; // Dummy command that does nothing
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetCreateTableCommandAsync(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = this.InternalCreateTableScriptText();
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetExistsTableCommandAsync(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM USER_TABLES 
                WHERE TABLE_NAME = :tableName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.TableDescription.TableName.ToUpper(); // Oracle stores identifiers in upper case by default
            command.Parameters.Add(parameter);
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetExistsSchemaCommandAsync(DbConnection connection, DbTransaction transaction)
        {
            // In Oracle, we would check for user existence instead of schema
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM DBA_USERS 
                WHERE USERNAME = :userName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":userName";
            parameter.Value = this.TableDescription.SchemaName?.ToUpper() ?? connection.Database.ToUpper();
            command.Parameters.Add(parameter);
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetDropTableCommandAsync(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DROP TABLE {this.oracleObjectNames.QuotedTableName}";
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetExistsColumnCommandAsync(string columnName, DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM USER_TAB_COLUMNS 
                WHERE TABLE_NAME = :tableName 
                AND COLUMN_NAME = :columnName";

            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = ":tableName";
            tableParameter.Value = this.TableDescription.TableName.ToUpper();
            command.Parameters.Add(tableParameter);

            var columnParameter = command.CreateParameter();
            columnParameter.ParameterName = ":columnName";
            columnParameter.Value = columnName.ToUpper();
            command.Parameters.Add(columnParameter);
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetAddColumnCommandAsync(string columnName, DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;

            var column = this.TableDescription.Columns.FirstOrDefault(c => 
                string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));

            if (column == null)
                throw new Exception($"Column {columnName} does not exist in table {this.TableDescription.TableName}");

            var columnTypeString = this.GetOracleColumnTypeString(column);
            var nullableString = column.AllowDBNull ? "NULL" : "NOT NULL";

            command.CommandText = $"ALTER TABLE {this.oracleObjectNames.QuotedTableName} ADD (\"{columnName}\" {columnTypeString} {nullableString})";
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetDropColumnCommandAsync(string columnName, DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"ALTER TABLE {this.oracleObjectNames.QuotedTableName} DROP COLUMN \"{columnName}\"";
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetExistsStoredProcedureCommandAsync(DbStoredProcedureType storedProcedureType, SyncFilter filter, DbConnection connection, DbTransaction transaction)
        {
            var procedureName = this.oracleObjectNames.GetStoredProcedureCommandName(storedProcedureType, filter);
            
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM USER_PROCEDURES 
                WHERE OBJECT_NAME = :procedureName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":procedureName";
            parameter.Value = procedureName.ToUpper();
            command.Parameters.Add(parameter);
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetCreateStoredProcedureCommandAsync(DbStoredProcedureType storedProcedureType, SyncFilter filter, DbConnection connection, DbTransaction transaction)
        {
            // This would require implementing the Oracle stored procedure generation for each type
            // For now, we'll return a placeholder implementation
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1 FROM DUAL"; // Placeholder
            
            // Full implementation would require building the stored procedure based on storedProcedureType
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetDropStoredProcedureCommandAsync(DbStoredProcedureType storedProcedureType, SyncFilter filter, DbConnection connection, DbTransaction transaction)
        {
            var procedureName = this.oracleObjectNames.GetStoredProcedureCommandName(storedProcedureType, filter);
            
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DROP PROCEDURE {procedureName}";
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetCreateTrackingTableCommandAsync(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = this.InternalCreateTrackingTableScriptText();
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetDropTrackingTableCommandAsync(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DROP TABLE {this.oracleObjectNames.QuotedTrackingTableName}";
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetExistsTrackingTableCommandAsync(DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM USER_TABLES 
                WHERE TABLE_NAME = :tableName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.oracleObjectNames.TrackingTableName.ToUpper(); // Oracle stores identifiers in upper case by default
            command.Parameters.Add(parameter);
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetExistsTriggerCommandAsync(DbTriggerType triggerType, DbConnection connection, DbTransaction transaction)
        {
            var triggerName = this.oracleObjectNames.GetTriggerCommandName(triggerType);
            
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT COUNT(*) 
                FROM USER_TRIGGERS 
                WHERE TRIGGER_NAME = :triggerName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":triggerName";
            parameter.Value = triggerName.ToUpper();
            command.Parameters.Add(parameter);
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetCreateTriggerCommandAsync(DbTriggerType triggerType, DbConnection connection, DbTransaction transaction)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = this.InternalCreateTriggerScriptText(triggerType);
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<DbCommand> GetDropTriggerCommandAsync(DbTriggerType triggerType, DbConnection connection, DbTransaction transaction)
        {
            var triggerName = this.oracleObjectNames.GetTriggerCommandName(triggerType);
            
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DROP TRIGGER {triggerName}";
            
            return command;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<IEnumerable<SyncColumn>> GetColumnsAsync(DbConnection connection, DbTransaction transaction)
        {
            var columns = new List<SyncColumn>();
            
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT 
                    c.COLUMN_NAME, 
                    c.DATA_TYPE,
                    c.DATA_LENGTH,
                    c.DATA_PRECISION,
                    c.DATA_SCALE,
                    c.NULLABLE,
                    (SELECT COUNT(*) FROM USER_CONS_COLUMNS ucc 
                     JOIN USER_CONSTRAINTS uc ON ucc.CONSTRAINT_NAME = uc.CONSTRAINT_NAME
                     WHERE ucc.TABLE_NAME = c.TABLE_NAME 
                     AND ucc.COLUMN_NAME = c.COLUMN_NAME 
                     AND uc.CONSTRAINT_TYPE = 'P') AS IS_PRIMARY_KEY,
                    c.CHAR_LENGTH
                FROM 
                    USER_TAB_COLUMNS c
                WHERE 
                    c.TABLE_NAME = :tableName
                ORDER BY 
                    c.COLUMN_ID";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.TableDescription.TableName.ToUpper();
            command.Parameters.Add(parameter);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync();

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var columnName = reader.GetString(0);
                        var dataType = reader.GetString(1);
                        var dataLength = !reader.IsDBNull(2) ? reader.GetInt32(2) : 0;
                        var precision = !reader.IsDBNull(3) ? reader.GetByte(3) : (byte)0;
                        var scale = !reader.IsDBNull(4) ? reader.GetByte(4) : (byte)0;
                        var allowNull = reader.GetString(5) == "Y";
                        var isPrimaryKey = !reader.IsDBNull(6) && reader.GetInt32(6) > 0;
                        var charLength = !reader.IsDBNull(7) ? reader.GetInt32(7) : 0;

                        var syncColumn = new SyncColumn(columnName)
                        {
                            OriginalTypeName = dataType,
                            MaxLength = dataType.Contains("CHAR") ? charLength : dataLength,
                            Precision = precision,
                            Scale = scale,
                            AllowDBNull = allowNull,
                            IsAutoIncrement = false // Oracle doesn't have auto-increment; it uses sequences + triggers
                        };
                        
                        if (isPrimaryKey)
                            syncColumn.ColumnName = columnName; // Just to satisfy the compiler, will look up actual property

                        columns.Add(syncColumn);
                    }
                }
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    connection.Close();
            }
            
            return columns;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<IEnumerable<DbRelationDefinition>> GetRelationsAsync(DbConnection connection, DbTransaction transaction)
        {
            var relations = new List<DbRelationDefinition>();
            
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT 
                    c.CONSTRAINT_NAME,
                    c.TABLE_NAME,
                    c.R_CONSTRAINT_NAME,
                    rc.TABLE_NAME AS REFERENCED_TABLE_NAME
                FROM 
                    USER_CONSTRAINTS c
                JOIN 
                    USER_CONSTRAINTS rc ON c.R_CONSTRAINT_NAME = rc.CONSTRAINT_NAME
                WHERE 
                    c.CONSTRAINT_TYPE = 'R' 
                AND 
                    c.TABLE_NAME = :tableName";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.TableDescription.TableName.ToUpper();
            command.Parameters.Add(parameter);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync();

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var constraintName = reader.GetString(0);
                        var tableName = reader.GetString(1);
                        var referencedConstraintName = reader.GetString(2);
                        var referencedTableName = reader.GetString(3);

                        // Now get columns for this foreign key
                        var columnsCommand = connection.CreateCommand();
                        columnsCommand.Transaction = transaction;
                        columnsCommand.CommandText = @"
                            SELECT 
                                c.COLUMN_NAME,
                                r.COLUMN_NAME AS REFERENCED_COLUMN_NAME
                            FROM 
                                USER_CONS_COLUMNS c
                            JOIN 
                                USER_CONS_COLUMNS r ON c.POSITION = r.POSITION
                            WHERE 
                                c.CONSTRAINT_NAME = :constraintName
                            AND 
                                r.CONSTRAINT_NAME = :referencedConstraintName
                            ORDER BY 
                                c.POSITION";

                        var conParameter = columnsCommand.CreateParameter();
                        conParameter.ParameterName = ":constraintName";
                        conParameter.Value = constraintName;
                        columnsCommand.Parameters.Add(conParameter);

                        var refConParameter = columnsCommand.CreateParameter();
                        refConParameter.ParameterName = ":referencedConstraintName";
                        refConParameter.Value = referencedConstraintName;
                        columnsCommand.Parameters.Add(refConParameter);

                        var foreignKeyColumns = new List<(string ColumnName, string ReferenceColumnName)>();

                        using (var columnsReader = await columnsCommand.ExecuteReaderAsync())
                        {
                            while (await columnsReader.ReadAsync())
                            {
                                var columnName = columnsReader.GetString(0);
                                var referencedColumnName = columnsReader.GetString(1);
                                foreignKeyColumns.Add((columnName, referencedColumnName));
                            }
                        }

                        if (foreignKeyColumns.Count > 0)
                        {
                            var columnsList = foreignKeyColumns.Select(c => new DbRelationColumnDefinition
                            {
                                KeyColumnName = c.ColumnName,
                                ReferenceColumnName = c.ReferenceColumnName
                            }).ToList();
                            
                            var relation = new DbRelationDefinition
                            {
                                ForeignKey = constraintName,
                                TableName = tableName,
                                ReferenceTableName = referencedTableName
                            };
                            
                            // Add columns manually if a constructor with columns parameter doesn't exist
                            // This is just a placeholder that will be replaced with the correct approach
                            
                            relations.Add(relation);
                        }
                    }
                }
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    connection.Close();
            }
            
            return relations;
        }

        // Override the abstract method from DbTableBuilder
        public override async Task<IEnumerable<SyncColumn>> GetPrimaryKeysAsync(DbConnection connection, DbTransaction transaction)
        {
            var primaryKeys = new List<SyncColumn>();
            
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
                SELECT 
                    cols.COLUMN_NAME
                FROM 
                    USER_CONSTRAINTS cons
                JOIN 
                    USER_CONS_COLUMNS cols ON cons.CONSTRAINT_NAME = cols.CONSTRAINT_NAME
                WHERE 
                    cons.TABLE_NAME = :tableName
                AND 
                    cons.CONSTRAINT_TYPE = 'P'
                ORDER BY 
                    cols.POSITION";

            var parameter = command.CreateParameter();
            parameter.ParameterName = ":tableName";
            parameter.Value = this.TableDescription.TableName.ToUpper();
            command.Parameters.Add(parameter);

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync();

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var columnName = reader.GetString(0);
                        var column = this.TableDescription.Columns.FirstOrDefault(c => 
                            string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
                        
                        if (column != null)
                            primaryKeys.Add(column);
                    }
                }
            }
            finally
            {
                if (!alreadyOpened && connection.State == ConnectionState.Open)
                    connection.Close();
            }
            
            return primaryKeys;
        }

        // Helper method to create DeleteMetadata parameters
        public void CreateDeleteMetadataParameters(DbCommand command, SyncFilter filter = null)
        {
            var oracleParameter = command.CreateParameter();
            oracleParameter.ParameterName = ":sync_row_timestamp";
            oracleParameter.DbType = DbType.Int64;
            command.Parameters.Add(oracleParameter);
        }
    }
} 