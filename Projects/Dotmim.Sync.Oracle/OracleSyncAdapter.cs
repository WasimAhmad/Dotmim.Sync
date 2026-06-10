using Dotmim.Sync.Builders;
using Dotmim.Sync.DatabaseStringParsers;
using Dotmim.Sync.Oracle.Builders;
using Dotmim.Sync.Oracle.Manager;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
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

            // Oracle has no table-valued parameters: bulk operations are not supported, always row-by-row.
            : base(tableDescription, scopeInfo, false)
        {
            this.OracleObjectNames = new OracleObjectNames(tableDescription, scopeInfo);
            this.OracleMetadata = new OracleDbMetadata();
        }

        /// <summary>
        /// Gets the parameter prefix for Oracle bind variables.
        /// </summary>
        public override string ParameterPrefix => ":";

        /// <inheritdoc />
        public override DbColumnNames GetParsedColumnNames(string name)
        {
            var columnParser = new ObjectParser(name, OracleObjectNames.LeftQuoteChar, OracleObjectNames.RightQuoteChar);
            return new DbColumnNames(columnParser.QuotedShortName, columnParser.NormalizedShortName);
        }

        /// <summary>
        /// Get the table builder. Table builder builds table, tracking table and triggers.
        /// </summary>
        public override DbTableBuilder GetTableBuilder() => new OracleTableBuilder(this.TableDescription, this.ScopeInfo);

        /// <inheritdoc/>
        public override (DbCommand, bool) GetCommand(SyncContext context, DbCommandType commandType, SyncFilter filter)
        {
            // Commands that have no Oracle implementation (handled elsewhere or not needed).
            switch (commandType)
            {
                case DbCommandType.UpdateMetadata:
                case DbCommandType.SelectMetadata:
                case DbCommandType.InsertTrigger:
                case DbCommandType.UpdateTrigger:
                case DbCommandType.DeleteTrigger:
                case DbCommandType.BulkTableType:
                case DbCommandType.PreDeleteRow:
                case DbCommandType.PreDeleteRows:
                case DbCommandType.PreInsertRow:
                case DbCommandType.PreInsertRows:
                case DbCommandType.PreUpdateRow:
                case DbCommandType.PreUpdateRows:
                    return (default, false);
            }

            var commandText = this.OracleObjectNames.GetCommandText(commandType, filter);

            if (string.IsNullOrEmpty(commandText))
                return (default, false);

            var command = new OracleCommand
            {
                CommandType = CommandType.Text,
                CommandText = commandText,

                // Bind variables are referenced by name in our generated SQL; ODP.NET binds by
                // position unless this is set, and the framework adds parameters in its own order.
                BindByName = true,
            };

            this.SetCommandParameters(commandType, command, filter);

            return (command, false);
        }

        /// <summary>
        /// Pre-creates the full parameter set for each command, with Oracle-safe types.
        /// The orchestrator skips its generic parameter creation when the command already
        /// has parameters — which matters twice here: ODP.NET throws when the framework
        /// assigns <c>DbType.Guid</c>, and ODP.NET raises ORA-01036 for parameters that
        /// have no matching bind variable (so only parameters the statement references
        /// are created).
        /// </summary>
        private void SetCommandParameters(DbCommandType commandType, OracleCommand command, SyncFilter filter)
        {
            switch (commandType)
            {
                case DbCommandType.SelectChanges:
                case DbCommandType.SelectChangesWithFilters:
                    this.AddParameter(command, "sync_min_timestamp", OracleDbType.Int64);
                    this.AddParameter(command, "sync_scope_id", OracleDbType.Raw, size: 16);
                    this.AddFilterParameters(command, filter);
                    break;

                case DbCommandType.SelectInitializedChanges:
                case DbCommandType.SelectInitializedChangesWithFilters:
                    this.AddParameter(command, "sync_min_timestamp", OracleDbType.Int64);
                    this.AddFilterParameters(command, filter);
                    break;

                case DbCommandType.SelectRow:
                    foreach (var column in this.TableDescription.GetPrimaryKeysColumns())
                        this.AddColumnParameter(command, column);
                    break;

                case DbCommandType.UpdateRow:
                case DbCommandType.InsertRow:
                case DbCommandType.UpdateRows:
                case DbCommandType.InsertRows:
                    foreach (var column in this.TableDescription.Columns.Where(c => !c.IsReadOnly))
                        this.AddColumnParameter(command, column);
                    this.AddApplyRowSyncParameters(command);
                    break;

                case DbCommandType.DeleteRow:
                case DbCommandType.DeleteRows:
                    foreach (var column in this.TableDescription.GetPrimaryKeysColumns())
                        this.AddColumnParameter(command, column);
                    this.AddApplyRowSyncParameters(command);
                    break;

                case DbCommandType.DeleteMetadata:
                    this.AddParameter(command, "sync_row_timestamp", OracleDbType.Int64);
                    break;

                case DbCommandType.Reset:
                    this.AddParameter(command, "sync_row_count", OracleDbType.Int32, ParameterDirection.Output);
                    break;

                // UpdateUntrackedRows, Disable/EnableConstraints: no parameters
            }
        }

        private void AddApplyRowSyncParameters(OracleCommand command)
        {
            this.AddParameter(command, "sync_scope_id", OracleDbType.Raw, size: 16);
            this.AddParameter(command, "sync_force_write", OracleDbType.Int64);
            this.AddParameter(command, "sync_min_timestamp", OracleDbType.Int64);
            this.AddParameter(command, "sync_row_count", OracleDbType.Int32, ParameterDirection.Output);
        }

        private void AddColumnParameter(OracleCommand command, SyncColumn column)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"{this.ParameterPrefix}{this.GetParsedColumnNames(column.ColumnName).NormalizedName}";
            parameter.SourceColumn = column.ColumnName;
            parameter.OracleDbType = this.OracleMetadata.GetOracleDbType(column.GetDbType(), column.MaxLength);
            command.Parameters.Add(parameter);
        }

        private void AddParameter(OracleCommand command, string name, OracleDbType oracleDbType, ParameterDirection direction = ParameterDirection.Input, int size = 0)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"{this.ParameterPrefix}{name}";
            parameter.OracleDbType = oracleDbType;
            parameter.Direction = direction;

            if (size > 0)
                parameter.Size = size;

            command.Parameters.Add(parameter);
        }

        private void AddFilterParameters(OracleCommand command, SyncFilter filter)
        {
            if (filter == null)
                return;

            foreach (var filterParameter in filter.Parameters)
            {
                // Mirror BaseOrchestrator.InternalSetSelectChangesParameters' type
                // resolution, substituting the Oracle-safe type mapping.
                string parameterName;
                DbType dbType;
                int maxLength;

                if (filterParameter.DbType.HasValue)
                {
                    var columnNames = this.GetParsedColumnNames(filterParameter.Name);
                    parameterName = columnNames.NormalizedName;
                    dbType = filterParameter.DbType.Value;
                    maxLength = filterParameter.MaxLength;
                }
                else
                {
                    var tableFilter = this.TableDescription.Schema?.Tables[filterParameter.TableName, filterParameter.SchemaName];
                    if (tableFilter == null)
                        throw new FilterParamTableNotExistsException(filterParameter.TableName);

                    var columnFilter = tableFilter.Columns[filterParameter.Name];
                    if (columnFilter == null)
                        throw new FilterParamColumnNotExistsException(filterParameter.Name, filterParameter.TableName);

                    var columnNames = this.GetTableBuilder().GetParsedColumnNames(columnFilter);
                    parameterName = columnNames.NormalizedName;
                    dbType = columnFilter.GetDbType();
                    maxLength = columnFilter.GetDataType() == typeof(string) && columnFilter.MaxLength > 0
                        ? columnFilter.MaxLength
                        : 0;
                }

                var parameter = command.CreateParameter();
                parameter.ParameterName = $"{this.ParameterPrefix}{parameterName}";
                parameter.OracleDbType = this.OracleMetadata.GetOracleDbType(dbType, maxLength);
                if (maxLength > 0)
                    parameter.Size = maxLength;

                command.Parameters.Add(parameter);
            }
        }

        /// <inheritdoc/>
        public override void AddCommandParameterValue(SyncContext context, DbParameter parameter, object value, DbCommand command, DbCommandType commandType)
        {
            if (value == null || value == DBNull.Value)
            {
                parameter.Value = DBNull.Value;
                return;
            }

            if (parameter is OracleParameter oracleParameter)
            {
                // Guid values bind to RAW(16) as Guid.ToByteArray() — ODP.NET has no
                // DbType.Guid. Guid columns may receive strings (HTTP/JSON rows).
                if (oracleParameter.OracleDbType == OracleDbType.Raw)
                {
                    if (value is Guid guid)
                    {
                        parameter.Value = guid.ToByteArray();
                        return;
                    }

                    var rawColumn = string.IsNullOrEmpty(parameter.SourceColumn) ? null : this.TableDescription.Columns[parameter.SourceColumn];
                    if (rawColumn != null && rawColumn.GetDbType() == DbType.Guid)
                    {
                        parameter.Value = SyncTypeConverter.TryConvertTo<Guid>(value).ToByteArray();
                        return;
                    }

                    // genuine binary column (or raw filter param): byte[] passes through,
                    // strings arrive base64-encoded from the serializer
                    parameter.Value = value is byte[] bytes ? bytes : SyncTypeConverter.TryConvertFromDbType(value, DbType.Binary);
                    return;
                }

                // Oracle has no boolean: NUMBER(1) with 0/1
                if (value is bool boolValue)
                {
                    parameter.Value = boolValue ? 1 : 0;
                    return;
                }
            }

            // convert on the COLUMN's schema type when known; parameter.DbType is not
            // trustworthy for provider-specific types (Raw -> Binary, Clob -> Object)
            var column = string.IsNullOrEmpty(parameter.SourceColumn) ? null : this.TableDescription.Columns[parameter.SourceColumn];

            parameter.Value = column != null
                ? SyncTypeConverter.TryConvertFromDbType(value, column.GetDbType())
                : SyncTypeConverter.TryConvertFromDbType(value, parameter.DbType);
        }

        /// <inheritdoc/>
        public override DbCommand EnsureCommandParameters(SyncContext context, DbCommand command, DbCommandType commandType, DbConnection connection, DbTransaction transaction, SyncFilter filter = null)
        {
            if (command is OracleCommand oracleCommand)
                oracleCommand.BindByName = true;

            // Defensive net: strip parameters that have no matching bind variable in the SQL.
            // GetCommand already creates exactly the right parameters, but interceptors or future
            // framework versions may add extras that would cause ORA-01036.
            for (var i = command.Parameters.Count - 1; i >= 0; i--)
            {
                var name = command.Parameters[i].ParameterName.TrimStart(':', '@');
                if (!Regex.IsMatch(command.CommandText, $@":{Regex.Escape(name)}\b", RegexOptions.IgnoreCase))
                    command.Parameters.RemoveAt(i);
            }

            // Coerce boolean parameters to NUMBER(1) before the command is prepared.
            foreach (DbParameter parameter in command.Parameters)
            {
                if (parameter is OracleParameter oracleParameter && oracleParameter.DbType == DbType.Boolean)
                    oracleParameter.OracleDbType = OracleDbType.Int32;
            }

            return command;
        }

        /// <inheritdoc/>
        public override Task ExecuteBatchCommandAsync(SyncContext context, DbCommand cmd, Guid senderScopeId, IEnumerable<SyncRow> arrayItems, SyncTable schemaChangesTable,
                                                      SyncTable failedRows, long? lastTimestamp, DbConnection connection, DbTransaction transaction = null)
            => throw new NotSupportedException("Bulk operations are not supported by the Oracle provider; rows are applied individually.");
    }
}
