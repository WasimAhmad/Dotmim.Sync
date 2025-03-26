using Dotmim.Sync.Builders;
using Dotmim.Sync.DatabaseStringParsers;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Oracle.Builders;
using Dotmim.Sync.Oracle.Manager;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;

namespace Dotmim.Sync.Oracle
{
    /// <inheritdoc />
    public class OracleSyncAdapter : DbSyncAdapter
    {
        /// <summary>
        /// Gets the OracleObjectNames.
        /// </summary>
        public OracleObjectNames OracleObjectNames { get; }

        /// <summary>
        /// Gets the OracleDbMetadata.
        /// </summary>
        public OracleDbMetadata OracleMetadata { get; }

        /// <inheritdoc />
        public OracleSyncAdapter(SyncTable tableDescription, ScopeInfo scopeInfo, bool useBulkOperations)
            : base(tableDescription, scopeInfo, useBulkOperations)
        {
            this.OracleObjectNames = new OracleObjectNames(tableDescription, scopeInfo);
            this.OracleMetadata = new OracleDbMetadata();
        }

        /// <inheritdoc/>
        public override DbColumnNames GetParsedColumnNames(string name)
        {
            var columnParser = new ObjectParser(name, OracleObjectNames.LeftQuote[0], OracleObjectNames.RightQuote[0]);
            return new DbColumnNames(columnParser.QuotedShortName, columnParser.NormalizedShortName);
        }

        /// <summary>
        /// Get the table builder. Table builder builds table, stored procedures and triggers.
        /// </summary>
        public override DbTableBuilder GetTableBuilder() => new OracleTableBuilder(this.TableDescription, this.ScopeInfo);

        /// <inheritdoc/>
        public override (DbCommand, bool) GetCommand(SyncContext context, DbCommandType commandType, SyncFilter filter)
        {
            using var command = new OracleCommand();
            bool isBatch;
            switch (commandType)
            {
                case DbCommandType.SelectChanges:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.OracleObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.SelectChanges, filter);
                    isBatch = false;
                    break;
                case DbCommandType.SelectInitializedChanges:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.OracleObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.SelectInitializedChanges, filter);
                    isBatch = false;
                    break;
                case DbCommandType.SelectInitializedChangesWithFilters:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.OracleObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.SelectInitializedChangesWithFilters, filter);
                    isBatch = false;
                    break;
                case DbCommandType.SelectChangesWithFilters:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.OracleObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.SelectChangesWithFilters, filter);
                    isBatch = false;
                    break;
                case DbCommandType.SelectRow:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.OracleObjectNames.GetCommandName(DbCommandType.SelectRow, filter);
                    isBatch = false;
                    break;
                case DbCommandType.UpdateRow:
                case DbCommandType.InsertRow:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.OracleObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.UpdateRow, filter);
                    isBatch = false;
                    break;
                case DbCommandType.UpdateRows:
                case DbCommandType.InsertRows:
                    command.CommandType = CommandType.StoredProcedure;
                    // Oracle lacks table-valued parameters, so we use individual row updates
                    command.CommandText = this.OracleObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.UpdateRow, filter);
                    isBatch = false;
                    break;
                case DbCommandType.DeleteRow:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.OracleObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.DeleteRow, filter);
                    isBatch = false;
                    break;
                case DbCommandType.DeleteRows:
                    command.CommandType = CommandType.StoredProcedure;
                    // Oracle lacks table-valued parameters, so we use individual row deletes
                    command.CommandText = this.OracleObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.DeleteRow, filter);
                    isBatch = false;
                    break;
                case DbCommandType.DisableConstraints:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.OracleObjectNames.GetCommandName(DbCommandType.DisableConstraints, filter);
                    isBatch = false;
                    break;
                case DbCommandType.EnableConstraints:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.OracleObjectNames.GetCommandName(DbCommandType.EnableConstraints, filter);
                    isBatch = false;
                    break;
                case DbCommandType.DeleteMetadata:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.OracleObjectNames.GetCommandName(DbCommandType.DeleteMetadata);
                    isBatch = false;
                    break;
                case DbCommandType.InsertTrigger:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.OracleObjectNames.GetTriggerCommandName(DbTriggerType.Insert);
                    isBatch = false;
                    break;
                case DbCommandType.UpdateTrigger:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.OracleObjectNames.GetTriggerCommandName(DbTriggerType.Update);
                    isBatch = false;
                    break;
                case DbCommandType.DeleteTrigger:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.OracleObjectNames.GetTriggerCommandName(DbTriggerType.Delete);
                    isBatch = false;
                    break;
                case DbCommandType.BulkTableType:
                    // Oracle doesn't support table-valued parameters like SQL Server
                    return (default, false);
                case DbCommandType.UpdateUntrackedRows:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.OracleObjectNames.GetCommandName(DbCommandType.UpdateUntrackedRows, filter);
                    isBatch = false;
                    break;
                case DbCommandType.Reset:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.OracleObjectNames.GetCommandName(DbCommandType.Reset, filter);
                    isBatch = false;
                    break;
                case DbCommandType.UpdateMetadata:
                case DbCommandType.SelectMetadata:
                case DbCommandType.PreDeleteRow:
                case DbCommandType.PreDeleteRows:
                case DbCommandType.PreInsertRow:
                case DbCommandType.PreInsertRows:
                case DbCommandType.PreUpdateRow:
                case DbCommandType.PreUpdateRows:
                    return (default, false);
                default:
                    throw new NotImplementedException($"This command type {commandType} is not implemented");
            }

            return (command, isBatch);
        }

        /// <inheritdoc/>
        public override void AddCommandParameterValue(SyncContext context, DbParameter parameter, object value, DbCommand command, DbCommandType commandType)
        {
            // Handle Oracle-specific parameter value conversions
            if (value == null)
            {
                parameter.Value = DBNull.Value;
                return;
            }

            // Handle special Oracle type conversions
            var oracleParameter = parameter as OracleParameter;
            if (oracleParameter != null)
            {
                // Convert boolean to number (0/1) since Oracle doesn't have a boolean type
                if (value is bool boolValue)
                {
                    oracleParameter.Value = boolValue ? 1 : 0;
                    return;
                }

                // Handle DateTime precision for Oracle
                if (value is DateTime dateTimeValue)
                {
                    // Ensure the date is within Oracle's valid range (01-JAN-0001 to 31-DEC-9999)
                    if (dateTimeValue.Year < 1 || dateTimeValue.Year > 9999)
                    {
                        oracleParameter.Value = DBNull.Value;
                        return;
                    }

                    oracleParameter.Value = dateTimeValue;
                    return;
                }

                // Handle large strings for CLOB
                if (value is string stringValue && oracleParameter.DbType == DbType.String && stringValue.Length > 4000)
                {
                    oracleParameter.OracleDbType = OracleDbType.Clob;
                    oracleParameter.Value = stringValue;
                    return;
                }

                // Handle large binary data for BLOB
                if (value is byte[] byteArrayValue && byteArrayValue.Length > 2000)
                {
                    oracleParameter.OracleDbType = OracleDbType.Blob;
                    oracleParameter.Value = byteArrayValue;
                    return;
                }
            }

            parameter.Value = value;
        }

        /// <inheritdoc/>
        public override DbCommand EnsureCommandParameters(SyncContext context, DbCommand command, DbCommandType commandType, DbConnection connection, DbTransaction transaction, SyncFilter filter = null)
        {
            // Oracle parameters are expected to be already configured in the stored procedures
            // or defined when the command is created
            return command;
        }

        /// <inheritdoc/>
        public override async Task ExecuteBatchCommandAsync(SyncContext context, DbCommand cmd, Guid senderScopeId, IEnumerable<SyncRow> arrayItems, SyncTable schemaChangesTable,
                                                            SyncTable failedRows, long? lastTimestamp, DbConnection connection, DbTransaction transaction = null)
        {
            // Oracle doesn't support table-valued parameters like SQL Server
            // Implement a row-by-row processing strategy
            var items = new List<SyncRow>(arrayItems);

            if (items.Count <= 0)
                return;

            var syncRowState = items[0].RowState;
            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                cmd.Transaction = transaction;

                // Process each row individually
                foreach (var row in items)
                {
                    // Reset parameters for each row
                    foreach (DbParameter parameter in cmd.Parameters)
                    {
                        if (parameter.ParameterName.StartsWith(":") && 
                            !parameter.ParameterName.Equals(":sync_scope_id", StringComparison.OrdinalIgnoreCase) &&
                            !parameter.ParameterName.Equals(":sync_min_timestamp", StringComparison.OrdinalIgnoreCase) &&
                            !parameter.ParameterName.Equals(":sync_force_write", StringComparison.OrdinalIgnoreCase))
                        {
                            var paramName = parameter.ParameterName.Substring(1); // Remove ':' prefix
                            var columnIndex = schemaChangesTable.Columns.IndexOf(paramName);
                            
                            if (columnIndex >= 0)
                            {
                                var value = row[columnIndex];
                                this.AddCommandParameterValue(context, parameter, value, cmd, DbCommandType.InsertRow);
                            }
                        }
                    }

                    // Set common parameters
                    if (cmd.Parameters.Contains(":sync_min_timestamp"))
                        ((OracleParameter)cmd.Parameters[":sync_min_timestamp"]).Value = lastTimestamp.HasValue ? lastTimestamp.Value : DBNull.Value;

                    if (cmd.Parameters.Contains(":sync_force_write"))
                        ((OracleParameter)cmd.Parameters[":sync_force_write"]).Value = context.SyncType == SyncType.Reinitialize || context.SyncType == SyncType.ReinitializeWithUpload ? 1 : 0;

                    if (cmd.Parameters.Contains(":sync_scope_id"))
                        ((OracleParameter)cmd.Parameters[":sync_scope_id"]).Value = senderScopeId.ToString();

                    // Execute the command for this row
                    using var dataReader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

                    while (await dataReader.ReadAsync().ConfigureAwait(false))
                    {
                        var failedRow = new SyncRow(schemaChangesTable, row.RowState);

                        for (var i = 0; i < dataReader.FieldCount; i++)
                        {
                            var columnValueObject = dataReader.GetValue(i);
                            var columnName = dataReader.GetName(i);

                            failedRow[columnName] = columnValueObject == DBNull.Value ? null : columnValueObject;
                        }

                        failedRows.Rows.Add(failedRow);
                    }

                    dataReader.Close();
                }
            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();
            }
        }
    }
} 