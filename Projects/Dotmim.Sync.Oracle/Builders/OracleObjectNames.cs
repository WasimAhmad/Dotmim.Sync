using Dotmim.Sync.Builders;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Dotmim.Sync.Oracle.Builders
{
    /// <summary>
    /// Generate all objects names for Oracle
    /// </summary>
    public class OracleObjectNames
    {
        private SyncTable tableDescription;
        private ScopeInfo scopeInfo;
        private const string oracleMaxIdentifierLength = "30";
        private readonly string leftQuoteIdentifier = "\"";
        private readonly string rightQuoteIdentifier = "\"";

        /// <summary>
        /// Gets the Oracle left quote identifier.
        /// </summary>
        public string LeftQuote => this.leftQuoteIdentifier;

        /// <summary>
        /// Gets the Oracle right quote identifier.
        /// </summary>
        public string RightQuote => this.rightQuoteIdentifier;

        /// <summary>
        /// Get the Oracle QUOTED FULL table name
        /// </summary>
        public string QuotedTableName
        {
            get
            {
                var tblName = this.tableDescription.TableName;
                var schemaName = this.tableDescription.SchemaName;

                if (string.IsNullOrEmpty(schemaName))
                    return $"{leftQuoteIdentifier}{tblName}{rightQuoteIdentifier}";
                else
                    return $"{leftQuoteIdentifier}{schemaName}{rightQuoteIdentifier}.{leftQuoteIdentifier}{tblName}{rightQuoteIdentifier}";
            }
        }

        /// <summary>
        /// Get the Oracle tracking quoted FULL table name
        /// </summary>
        public string QuotedTrackingTableName
        {
            get
            {
                var tblName = this.TrackingTableName;
                var schemaName = this.tableDescription.SchemaName;

                if (string.IsNullOrEmpty(schemaName))
                    return $"{leftQuoteIdentifier}{tblName}{rightQuoteIdentifier}";
                else
                    return $"{leftQuoteIdentifier}{schemaName}{rightQuoteIdentifier}.{leftQuoteIdentifier}{tblName}{rightQuoteIdentifier}";
            }
        }

        /// <summary>
        /// Get the Oracle tracking table name (not quoted, not full)
        /// </summary>
        public string TrackingTableName => GetTrackingTableName(this.tableDescription.TableName);

        /// <summary>
        /// Create an instance of OracleObjectNames.
        /// </summary>
        public OracleObjectNames(SyncTable tableDescription, ScopeInfo scopeInfo)
        {
            this.tableDescription = tableDescription;
            this.scopeInfo = scopeInfo;
        }

        /// <summary>
        /// Get the name of the tracking table for a given table name
        /// </summary>
        public static string GetTrackingTableName(string tableName)
        {
            string trackingName = $"{tableName.ToUpperInvariant()}_TRACK";
            
            // Oracle identifiers are limited to 30 characters
            if (trackingName.Length > 30)
                trackingName = trackingName.Substring(0, 30);
            
            return trackingName;
        }

        /// <summary>
        /// Gets the Oracle stored procedure name
        /// </summary>
        public string GetStoredProcedureCommandName(DbStoredProcedureType storedProcedureType, SyncFilter filter = null)
        {
            string command;
            switch (storedProcedureType)
            {
                case DbStoredProcedureType.SelectChanges:
                    command = $"{this.tableDescription.TableName.ToUpperInvariant()}_SELECTCHANGES";
                    break;
                case DbStoredProcedureType.SelectInitializedChanges:
                    command = $"{this.tableDescription.TableName.ToUpperInvariant()}_SELECTINITCHANGES";
                    break;
                case DbStoredProcedureType.SelectChangesWithFilters:
                    if (filter == null)
                        throw new ArgumentNullException(nameof(filter), "Filter is required for SelectChangesWithFilters stored procedure");
                    command = $"{this.tableDescription.TableName.ToUpperInvariant()}_SELECTCHANGES_{filter.GetFilterName()}";
                    break;
                case DbStoredProcedureType.SelectInitializedChangesWithFilters:
                    if (filter == null)
                        throw new ArgumentNullException(nameof(filter), "Filter is required for SelectInitializedChangesWithFilters stored procedure");
                    command = $"{this.tableDescription.TableName.ToUpperInvariant()}_SELECTINITCHANGES_{filter.GetFilterName()}";
                    break;
                case DbStoredProcedureType.UpdateRow:
                    command = $"{this.tableDescription.TableName.ToUpperInvariant()}_UPDATE";
                    break;
                case DbStoredProcedureType.DeleteRow:
                    command = $"{this.tableDescription.TableName.ToUpperInvariant()}_DELETE";
                    break;
                case DbStoredProcedureType.Reset:
                    command = $"{this.tableDescription.TableName.ToUpperInvariant()}_RESET";
                    break;
                case DbStoredProcedureType.BulkTableType:
                case DbStoredProcedureType.BulkUpdateRows:
                case DbStoredProcedureType.BulkDeleteRows:
                    // Oracle doesn't support table-valued parameters, so we don't need these procedures
                    return string.Empty;
                default:
                    throw new ArgumentException($"Stored procedure type {storedProcedureType} is not supported");
            }

            // Oracle identifiers are limited to 30 characters
            if (command.Length > 30)
                command = command.Substring(0, 30);

            return command;
        }

        /// <summary>
        /// Gets the SQL command text for various operations
        /// </summary>
        public string GetCommandName(DbCommandType commandType, SyncFilter filter = null)
        {
            switch (commandType)
            {
                case DbCommandType.SelectRow:
                    return GetSelectRowCommandText();
                case DbCommandType.DisableConstraints:
                    return GetDisableConstraintsCommandText();
                case DbCommandType.EnableConstraints:
                    return GetEnableConstraintsCommandText();
                case DbCommandType.DeleteMetadata:
                    return GetDeleteMetadataCommandText();
                case DbCommandType.UpdateUntrackedRows:
                    return GetUpdateUntrackedRowsCommandText();
                case DbCommandType.Reset:
                    return GetResetCommandText();
                default:
                    throw new ArgumentException($"Command type {commandType} is not supported in GetCommandName");
            }
        }

        /// <summary>
        /// Gets the Oracle trigger command text
        /// </summary>
        public string GetTriggerCommandName(DbTriggerType triggerType)
        {
            var triggerName = $"{this.tableDescription.TableName}_TRRIG_{triggerType.ToString().ToUpperInvariant()}";
            
            // Oracle identifiers are limited to 30 characters
            if (triggerName.Length > 30)
                triggerName = triggerName.Substring(0, 30);
            
            return triggerName;
        }

        private string GetSelectRowCommandText()
        {
            var stringBuilder = new System.Text.StringBuilder();
            stringBuilder.AppendLine($"SELECT * FROM {this.QuotedTableName}");
            stringBuilder.Append("WHERE ");

            string and = string.Empty;
            foreach (var pkColumn in this.tableDescription.GetPrimaryKeysColumns())
            {
                stringBuilder.Append($"{and}{leftQuoteIdentifier}{pkColumn.ColumnName}{rightQuoteIdentifier} = :{pkColumn.ColumnName}");
                and = " AND ";
            }

            return stringBuilder.ToString();
        }

        private string GetDisableConstraintsCommandText()
        {
            var stringBuilder = new System.Text.StringBuilder();
            
            // Oracle approach to disable constraints is to use the ALTER TABLE command with DISABLE CONSTRAINT
            stringBuilder.AppendLine($"BEGIN");
            
            // Get all foreign key constraints that reference this table
            stringBuilder.AppendLine($"  FOR c IN (SELECT c.constraint_name, c.table_name");
            stringBuilder.AppendLine($"            FROM user_constraints c");
            stringBuilder.AppendLine($"            JOIN user_constraints r ON c.r_constraint_name = r.constraint_name");
            stringBuilder.AppendLine($"            WHERE r.table_name = '{this.tableDescription.TableName}'");
            stringBuilder.AppendLine($"            AND c.constraint_type = 'R')");
            stringBuilder.AppendLine($"  LOOP");
            stringBuilder.AppendLine($"    EXECUTE IMMEDIATE 'ALTER TABLE \"' || c.table_name || '\" DISABLE CONSTRAINT \"' || c.constraint_name || '\"';");
            stringBuilder.AppendLine($"  END LOOP;");
            
            // Get foreign key constraints on this table
            stringBuilder.AppendLine($"  FOR c IN (SELECT constraint_name");
            stringBuilder.AppendLine($"            FROM user_constraints");
            stringBuilder.AppendLine($"            WHERE table_name = '{this.tableDescription.TableName}'");
            stringBuilder.AppendLine($"            AND constraint_type = 'R')");
            stringBuilder.AppendLine($"  LOOP");
            stringBuilder.AppendLine($"    EXECUTE IMMEDIATE 'ALTER TABLE {this.QuotedTableName} DISABLE CONSTRAINT \"' || c.constraint_name || '\"';");
            stringBuilder.AppendLine($"  END LOOP;");
            
            stringBuilder.AppendLine($"END;");
            
            return stringBuilder.ToString();
        }

        private string GetEnableConstraintsCommandText()
        {
            var stringBuilder = new System.Text.StringBuilder();
            
            // Oracle approach to enable constraints is to use the ALTER TABLE command with ENABLE CONSTRAINT
            stringBuilder.AppendLine($"BEGIN");
            
            // Enable foreign key constraints on this table first
            stringBuilder.AppendLine($"  FOR c IN (SELECT constraint_name");
            stringBuilder.AppendLine($"            FROM user_constraints");
            stringBuilder.AppendLine($"            WHERE table_name = '{this.tableDescription.TableName}'");
            stringBuilder.AppendLine($"            AND constraint_type = 'R')");
            stringBuilder.AppendLine($"  LOOP");
            stringBuilder.AppendLine($"    EXECUTE IMMEDIATE 'ALTER TABLE {this.QuotedTableName} ENABLE CONSTRAINT \"' || c.constraint_name || '\"';");
            stringBuilder.AppendLine($"  END LOOP;");
            
            // Then enable all foreign key constraints that reference this table
            stringBuilder.AppendLine($"  FOR c IN (SELECT c.constraint_name, c.table_name");
            stringBuilder.AppendLine($"            FROM user_constraints c");
            stringBuilder.AppendLine($"            JOIN user_constraints r ON c.r_constraint_name = r.constraint_name");
            stringBuilder.AppendLine($"            WHERE r.table_name = '{this.tableDescription.TableName}'");
            stringBuilder.AppendLine($"            AND c.constraint_type = 'R')");
            stringBuilder.AppendLine($"  LOOP");
            stringBuilder.AppendLine($"    EXECUTE IMMEDIATE 'ALTER TABLE \"' || c.table_name || '\" ENABLE CONSTRAINT \"' || c.constraint_name || '\"';");
            stringBuilder.AppendLine($"  END LOOP;");
            
            stringBuilder.AppendLine($"END;");
            
            return stringBuilder.ToString();
        }

        private string GetDeleteMetadataCommandText()
        {
            return $"DELETE FROM {this.QuotedTrackingTableName} WHERE {leftQuoteIdentifier}sync_row_is_tombstone{rightQuoteIdentifier} = 1";
        }

        private string GetUpdateUntrackedRowsCommandText()
        {
            return $@"MERGE INTO {this.QuotedTrackingTableName} t
                     USING (
                         SELECT p.*, :sync_min_timestamp as sync_min_timestamp 
                         FROM {this.QuotedTableName} p
                         LEFT JOIN {this.QuotedTrackingTableName} t ON {this.GetPrimaryKeyJoinClause("p", "t")}
                         WHERE t.{leftQuoteIdentifier}update_scope_id{rightQuoteIdentifier} IS NULL
                     ) s
                     ON ({this.GetPrimaryKeyJoinClause("s", "t")})
                     WHEN NOT MATCHED THEN
                         INSERT ({string.Join(", ", this.tableDescription.PrimaryKeys.Select(pk => $"{leftQuoteIdentifier}{pk}{rightQuoteIdentifier}"))},
                                 {leftQuoteIdentifier}update_scope_id{rightQuoteIdentifier},
                                 {leftQuoteIdentifier}sync_row_is_tombstone{rightQuoteIdentifier},
                                 {leftQuoteIdentifier}update_timestamp{rightQuoteIdentifier},
                                 {leftQuoteIdentifier}last_change_datetime{rightQuoteIdentifier})
                         VALUES ({string.Join(", ", this.tableDescription.PrimaryKeys.Select(pk => $"s.{leftQuoteIdentifier}{pk}{rightQuoteIdentifier}"))},
                                NULL, 
                                0, 
                                s.sync_min_timestamp, 
                                SYSDATE)";
        }

        private string GetResetCommandText()
        {
            return $"TRUNCATE TABLE {this.QuotedTrackingTableName}";
        }

        private string GetPrimaryKeyJoinClause(string leftAlias, string rightAlias)
        {
            return string.Join(" AND ", this.tableDescription.PrimaryKeys.Select(pk => 
                $"{leftAlias}.{leftQuoteIdentifier}{pk}{rightQuoteIdentifier} = {rightAlias}.{leftQuoteIdentifier}{pk}{rightQuoteIdentifier}"));
        }
    }
} 