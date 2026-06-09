using Dotmim.Sync.Builders;
using Dotmim.Sync.DatabaseStringParsers;
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

            return (command, false);
        }

        /// <inheritdoc/>
        public override void AddCommandParameterValue(SyncContext context, DbParameter parameter, object value, DbCommand command, DbCommandType commandType)
        {
            if (value == null || value == DBNull.Value)
            {
                parameter.Value = DBNull.Value;
                return;
            }

            // Oracle has no native boolean: uniqueidentifier columns are stored as RAW(16) (handled
            // natively by ODP.NET via DbType.Guid); booleans are stored as NUMBER(1).
            if (value is bool boolValue)
            {
                if (parameter is OracleParameter oracleBoolParameter)
                    oracleBoolParameter.OracleDbType = OracleDbType.Int32;

                parameter.Value = boolValue ? 1 : 0;
                return;
            }

            parameter.Value = SyncTypeConverter.TryConvertFromDbType(value, parameter.DbType);
        }

        /// <inheritdoc/>
        public override DbCommand EnsureCommandParameters(SyncContext context, DbCommand command, DbCommandType commandType, DbConnection connection, DbTransaction transaction, SyncFilter filter = null)
        {
            if (command is OracleCommand oracleCommand)
                oracleCommand.BindByName = true;

            // Coerce boolean parameters to NUMBER(1) before the command is prepared.
            // (DbType.Guid is handled natively by ODP.NET as RAW(16).)
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
