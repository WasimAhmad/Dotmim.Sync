using Dotmim.Sync.Builders;
using Dotmim.Sync.Oracle;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Dotmim.Sync.Tests.UnitTests.Oracle
{
    public class OracleSyncAdapterTests
    {
        private static (OracleSyncAdapter Adapter, SyncContext Context) BuildAdapter()
        {
            var table = new SyncTable("Product");
            table.Columns.Add(new SyncColumn("ProductId", typeof(Guid)));
            table.Columns.Add(new SyncColumn("Name", typeof(string)));
            table.Columns.Add(new SyncColumn("IsActive", typeof(bool)));
            table.PrimaryKeys.Add("ProductId");

            var schema = new SyncSet();
            schema.Tables.Add(table);

            var scopeInfo = new ScopeInfo { Name = "DefaultScope", Setup = new SyncSetup("Product") };
            var adapter = new OracleSyncAdapter(table, scopeInfo, useBulkOperations: false);
            var context = new SyncContext(Guid.NewGuid(), "DefaultScope");
            return (adapter, context);
        }

        private static string[] ParameterNames(DbCommand command)
            => command.Parameters.Cast<DbParameter>().Select(p => p.ParameterName).ToArray();

        [Fact]
        public void GetCommand_SelectRow_CreatesOnlyPrimaryKeyParameters()
        {
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.SelectRow, null);

            Assert.Equal(new[] { ":ProductId" }, ParameterNames(command));
        }

        [Fact]
        public void GetCommand_UpdateRow_CreatesColumnsAndSyncParameters()
        {
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.UpdateRow, null);

            var names = ParameterNames(command);
            Assert.Equal(
                new[] { ":ProductId", ":Name", ":IsActive", ":sync_scope_id", ":sync_force_write", ":sync_min_timestamp", ":sync_row_count" },
                names);

            var parameters = command.Parameters.Cast<OracleParameter>().ToArray();

            // column parameters carry SourceColumn so the framework can match row values
            Assert.Equal("ProductId", parameters[0].SourceColumn);
            Assert.Equal(OracleDbType.Raw, parameters[0].OracleDbType); // Guid -> RAW(16)
            Assert.Equal(OracleDbType.Int32, parameters[2].OracleDbType); // bool -> NUMBER-ish int

            var rowCount = parameters.Single(p => p.ParameterName == ":sync_row_count");
            Assert.Equal(ParameterDirection.Output, rowCount.Direction);
            // Fix 1 (ODP.NET dual-API): type MUST be set via DbType so the output value
            // comes back as a boxed int. Setting only OracleDbType returns OracleDecimal,
            // which the orchestrator's hard-cast (int)Value cannot unbox.
            // ODP.NET output behaviour cannot be asserted statically — it is on the Task 16 live checklist.
            Assert.Equal(OracleDbType.Int32, rowCount.OracleDbType);
            Assert.Equal(DbType.Int32, rowCount.DbType);

            var scopeId = parameters.Single(p => p.ParameterName == ":sync_scope_id");
            Assert.Equal(OracleDbType.Raw, scopeId.OracleDbType);
        }

        [Fact]
        public void GetCommand_DeleteMetadata_CreatesOnlyRowTimestampParameter()
        {
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.DeleteMetadata, null);

            Assert.Equal(new[] { ":sync_row_timestamp" }, ParameterNames(command));
        }

        [Fact]
        public void GetCommand_Reset_CreatesOnlyOutputRowCount()
        {
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.Reset, null);

            var parameter = command.Parameters.Cast<OracleParameter>().Single();
            Assert.Equal(":sync_row_count", parameter.ParameterName);
            Assert.Equal(ParameterDirection.Output, parameter.Direction);
            // Fix 1: DbType must be Int32 so ODP.NET returns a boxed int on output, not OracleDecimal
            Assert.Equal(DbType.Int32, parameter.DbType);
        }

        [Fact]
        public void GetCommand_EveryParameterIsReferencedInTheSql_AndViceVersa()
        {
            // ORA-01036 guard: ODP.NET (BindByName) errors on parameters with no matching
            // bind variable AND on bind variables with no parameter. Both directions must
            // hold for every command type the adapter emits.
            var (adapter, context) = BuildAdapter();

            var commandTypes = new[]
            {
                DbCommandType.SelectChanges, DbCommandType.SelectInitializedChanges,
                DbCommandType.SelectRow, DbCommandType.UpdateRow, DbCommandType.InsertRow,
                DbCommandType.UpdateRows, DbCommandType.InsertRows, DbCommandType.DeleteRow,
                DbCommandType.DeleteRows, DbCommandType.DeleteMetadata, DbCommandType.Reset,
                DbCommandType.UpdateUntrackedRows, DbCommandType.DisableConstraints,
                DbCommandType.EnableConstraints,
            };

            foreach (var commandType in commandTypes)
            {
                var (command, _) = adapter.GetCommand(context, commandType, null);
                Assert.NotNull(command);

                // strip PL/SQL local variable declarations (v_xxx) and trigger pseudo-rows
                // are not bind variables; bind variables are :name tokens not preceded by a word char.
                // Trigger DDL never flows through GetCommand so OLD/NEW filtering is unnecessary and
                // would mask a genuine bind for a column named e.g. NewStatus.
                // The character class includes $ and # which are legal Oracle identifier characters.
                var bindNames = Regex.Matches(command.CommandText, @"(?<![\w:]):(?<name>[A-Za-z_][A-Za-z0-9_$#]*)")
                    .Cast<Match>()
                    .Select(m => m.Groups["name"].Value)
                    .Select(n => n.ToUpperInvariant())
                    .ToHashSet();

                var parameterNames = command.Parameters.Cast<DbParameter>()
                    .Select(p => p.ParameterName.TrimStart(':').ToUpperInvariant())
                    .ToHashSet();

                Assert.True(parameterNames.SetEquals(bindNames),
                    $"{commandType}: parameters [{string.Join(",", parameterNames)}] != binds [{string.Join(",", bindNames)}]");
            }
        }

        [Fact]
        public void AddCommandParameterValue_ConvertsGuidValuesToByteArrayForRawParameters()
        {
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.UpdateRow, null);

            var guid = Guid.NewGuid();
            var pk = command.Parameters.Cast<OracleParameter>().Single(p => p.ParameterName == ":ProductId");
            var scopeId = command.Parameters.Cast<OracleParameter>().Single(p => p.ParameterName == ":sync_scope_id");

            // Guid value (scope id path)
            adapter.AddCommandParameterValue(context, scopeId, guid, command, DbCommandType.UpdateRow);
            Assert.Equal(guid.ToByteArray(), scopeId.Value);

            // guid-string value (HTTP/JSON row path) on a Guid column parameter
            adapter.AddCommandParameterValue(context, pk, guid.ToString(), command, DbCommandType.UpdateRow);
            Assert.Equal(guid.ToByteArray(), pk.Value);

            // byte[16] passes through unchanged
            adapter.AddCommandParameterValue(context, pk, guid.ToByteArray(), command, DbCommandType.UpdateRow);
            Assert.Equal(guid.ToByteArray(), pk.Value);
        }

        [Fact]
        public void AddCommandParameterValue_CoercesBooleansAndNulls()
        {
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.UpdateRow, null);
            var isActive = command.Parameters.Cast<OracleParameter>().Single(p => p.ParameterName == ":IsActive");

            adapter.AddCommandParameterValue(context, isActive, true, command, DbCommandType.UpdateRow);
            Assert.Equal(1, isActive.Value);

            adapter.AddCommandParameterValue(context, isActive, false, command, DbCommandType.UpdateRow);
            Assert.Equal(0, isActive.Value);

            adapter.AddCommandParameterValue(context, isActive, null, command, DbCommandType.UpdateRow);
            Assert.Equal(DBNull.Value, isActive.Value);
        }

        [Fact]
        public void EnsureCommandParameters_RemovesParametersNotReferencedInSql()
        {
            // defensive net: GetCommand creates exactly the right parameters, but if an
            // interceptor or future framework version adds extras, they must be stripped.
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.SelectRow, null);

            var extra = command.CreateParameter();
            extra.ParameterName = ":sync_scope_id"; // not referenced by SelectRow SQL
            command.Parameters.Add(extra);

            using var connection = new OracleConnection();
            adapter.EnsureCommandParameters(context, command, DbCommandType.SelectRow, connection, null);

            Assert.Equal(new[] { ":ProductId" }, ParameterNames(command));
        }

        [Fact]
        public void EnsureCommandParameters_DoesNotMatchOnNamePrefix()
        {
            var (adapter, context) = BuildAdapter();

            using var connection = new OracleConnection();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"Product\" WHERE \"ProductId\" = :ProductId";

            var prefix = command.CreateParameter();
            prefix.ParameterName = ":Product"; // strict prefix of :ProductId -> must be stripped
            command.Parameters.Add(prefix);
            var exact = command.CreateParameter();
            exact.ParameterName = ":ProductId";
            command.Parameters.Add(exact);

            adapter.EnsureCommandParameters(context, command, DbCommandType.SelectRow, connection, null);

            Assert.Equal(new[] { ":ProductId" }, ParameterNames(command));
        }

        [Fact]
        public void GetCommand_SelectChangesWithFilters_CreatesSafeFilterParameters()
        {
            var (adapter, context) = BuildAdapter();

            var filter = new SyncFilter("Product");
            filter.Parameters.Add(new SyncFilterParameter { Name = "CustomerId", DbType = DbType.Guid });
            filter.Parameters.Add(new SyncFilterParameter { Name = "Region", DbType = DbType.String }); // MaxLength 0

            var (command, _) = adapter.GetCommand(context, DbCommandType.SelectChangesWithFilters, filter);
            var parameters = command.Parameters.Cast<OracleParameter>().ToArray();

            var customerId = parameters.Single(p => p.ParameterName == ":CustomerId");
            Assert.Equal(OracleDbType.Raw, customerId.OracleDbType);

            // a comparison bind must never be CLOB (ORA-00932) nor report DbType.Object
            var region = parameters.Single(p => p.ParameterName == ":Region");
            Assert.Equal(OracleDbType.Varchar2, region.OracleDbType);

            // guid-strings on a Raw filter param (no SourceColumn) parse as Guid, not base64
            var guid = Guid.NewGuid();
            adapter.AddCommandParameterValue(context, customerId, guid.ToString(), command, DbCommandType.SelectChangesWithFilters);
            Assert.Equal(guid.ToByteArray(), customerId.Value);
        }

        [Fact]
        public void AddCommandParameterValue_CoercesConvertedBooleanFormsToNumbers()
        {
            var (adapter, context) = BuildAdapter();
            var (command, _) = adapter.GetCommand(context, DbCommandType.UpdateRow, null);
            var isActive = command.Parameters.Cast<OracleParameter>().Single(p => p.ParameterName == ":IsActive");

            adapter.AddCommandParameterValue(context, isActive, "true", command, DbCommandType.UpdateRow);
            Assert.Equal(1, isActive.Value);

            adapter.AddCommandParameterValue(context, isActive, 1L, command, DbCommandType.UpdateRow);
            Assert.Equal(1, isActive.Value);

            adapter.AddCommandParameterValue(context, isActive, "0", command, DbCommandType.UpdateRow);
            Assert.Equal(0, isActive.Value);
        }
    }
}
