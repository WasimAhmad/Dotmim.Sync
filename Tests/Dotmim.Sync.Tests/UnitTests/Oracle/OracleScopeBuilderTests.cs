using Dotmim.Sync.Oracle.Builders;
using Oracle.ManagedDataAccess.Client;
using System.Data;
using System.Data.Common;
using System.Linq;
using Xunit;

namespace Dotmim.Sync.Tests.UnitTests.Oracle
{
    public class OracleScopeBuilderTests
    {
        private static string[] ParameterNames(DbCommand command)
            => command.Parameters.Cast<DbParameter>().Select(p => p.ParameterName).ToArray();

        [Fact]
        public void CreateScopeInfoTable_UsesCanonicalColumnsAndNameKey()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var text = builder.GetCreateScopeInfoTableCommand(connection, null).CommandText;

            Assert.Contains("\"sync_scope_name\"", text);
            Assert.Contains("\"sync_scope_schema\"", text);
            Assert.Contains("\"sync_scope_setup\"", text);
            Assert.Contains("\"sync_scope_version\"", text);
            Assert.Contains("\"sync_scope_last_clean_timestamp\"", text);
            Assert.Contains("\"sync_scope_properties\"", text);
            // scope_info is keyed by name only — it must NOT contain a scope id column
            Assert.DoesNotContain("sync_scope_id", text);
            Assert.Contains("PRIMARY KEY (\"sync_scope_name\")", text);
        }

        [Fact]
        public void CreateScopeInfoClientTable_UsesCanonicalColumnsAndCompositeKey()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var text = builder.GetCreateScopeInfoClientTableCommand(connection, null).CommandText;

            foreach (var col in new[]
            {
                "\"sync_scope_id\"", "\"sync_scope_name\"", "\"sync_scope_hash\"",
                "\"sync_scope_parameters\"", "\"scope_last_sync_timestamp\"",
                "\"scope_last_server_sync_timestamp\"", "\"scope_last_sync_duration\"",
                "\"scope_last_sync\"", "\"sync_scope_errors\"", "\"sync_scope_properties\"",
            })
            {
                Assert.Contains(col, text);
            }

            Assert.Contains("PRIMARY KEY (\"sync_scope_id\", \"sync_scope_name\", \"sync_scope_hash\")", text);
            // invented legacy columns must be gone
            Assert.DoesNotContain("sync_scope_client_id", text);
            Assert.DoesNotContain("sync_scope_filters", text);
        }

        [Fact]
        public void InsertScopeInfo_UsesCanonicalParameterNames_AndReturnsRow()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var command = builder.GetInsertScopeInfoCommand(connection, null);
            var names = ParameterNames(command);

            Assert.Contains(":sync_scope_name", names);
            Assert.Contains(":sync_scope_schema", names);
            Assert.Contains(":sync_scope_setup", names);
            Assert.Contains(":sync_scope_version", names);
            Assert.Contains(":sync_scope_last_clean_timestamp", names);
            Assert.Contains(":sync_scope_properties", names);
            // the orchestrator reads the saved row back from a result set
            Assert.Contains("DBMS_SQL.RETURN_RESULT", command.CommandText);
        }

        [Fact]
        public void UpdateScopeInfoClient_UsesCanonicalParameterNames_AndReturnsRow()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var command = builder.GetUpdateScopeInfoClientCommand(connection, null);
            var names = ParameterNames(command);

            Assert.Contains(":sync_scope_id", names);
            Assert.Contains(":sync_scope_name", names);
            Assert.Contains(":sync_scope_hash", names);
            Assert.Contains(":sync_scope_parameters", names);
            Assert.Contains(":scope_last_sync_timestamp", names);
            Assert.Contains(":scope_last_server_sync_timestamp", names);
            Assert.Contains(":scope_last_sync_duration", names);
            Assert.Contains(":scope_last_sync", names);
            Assert.Contains(":sync_scope_errors", names);
            Assert.Contains(":sync_scope_properties", names);
            Assert.Contains("DBMS_SQL.RETURN_RESULT", command.CommandText);

            // ODP.NET rejects DbType.Guid: scope ids bind as 36-char strings and the SQL
            // converts them to RAW(16) in Guid.ToByteArray() order.
            var scopeIdParameter = command.Parameters.Cast<DbParameter>().Single(p => p.ParameterName == ":sync_scope_id");
            Assert.Equal(DbType.String, scopeIdParameter.DbType);
            Assert.Equal(36, scopeIdParameter.Size);
            Assert.Contains("HEXTORAW", command.CommandText);
        }

        [Fact]
        public void ExistsScopeInfoTable_ComparesCasePreservedName()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var command = builder.GetExistsScopeInfoTableCommand(connection, null);
            var parameter = command.Parameters.Cast<DbParameter>().Single();

            // C2: creation quotes "scope_info" (case-preserved) so the exists
            // check must compare the same string, NOT 'SCOPE_INFO'.
            Assert.Equal("scope_info", parameter.Value);
        }

        [Fact]
        public void GetScopeInfo_FiltersByNameOnly()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var command = builder.GetScopeInfoCommand(connection, null);

            Assert.Contains(":sync_scope_name", command.CommandText);
            Assert.DoesNotContain(":sync_scope_id", command.CommandText);
        }

        [Fact]
        public void ExistsScopeInfoClient_FiltersByNameIdAndHash()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var text = builder.GetExistsScopeInfoClientCommand(connection, null).CommandText;

            Assert.Contains(":sync_scope_name", text);
            Assert.Contains(":sync_scope_id", text);
            Assert.Contains(":sync_scope_hash", text);
        }

        [Fact]
        public void ScopeInfoClientCommands_ConvertScopeIdStringToRaw()
        {
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var commands = new[]
            {
                builder.GetExistsScopeInfoClientCommand(connection, null),
                builder.GetScopeInfoClientCommand(connection, null),
                builder.GetInsertScopeInfoClientCommand(connection, null),
                builder.GetUpdateScopeInfoClientCommand(connection, null),
                builder.GetDeleteScopeInfoClientCommand(connection, null),
            };

            foreach (var command in commands)
            {
                // every scope-id usage must go through the HEXTORAW conversion;
                // a bare RAW-vs-string comparison would never match.
                Assert.Contains("HEXTORAW", command.CommandText);
                Assert.DoesNotContain("= :sync_scope_id", command.CommandText);

                var scopeIdParameter = command.Parameters.Cast<DbParameter>().Single(p => p.ParameterName == ":sync_scope_id");
                Assert.Equal(DbType.String, scopeIdParameter.DbType);
            }
        }
    }
}
