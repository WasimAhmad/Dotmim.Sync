using Dotmim.Sync;
using Dotmim.Sync.Oracle.Builders;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
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

        [Fact]
        public void SaveCommands_ParameterDbTypes_SurviveFrameworkConversion()
        {
            // The orchestrator converts every non-null value with
            // SyncTypeConverter.TryConvertFromDbType(value, parameter.DbType) before
            // assignment (no provider hook). Every declared DbType must therefore be one
            // the converter maps losslessly for the value shape the orchestrator passes.
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");

            var commands = new[]
            {
                builder.GetInsertScopeInfoCommand(connection, null),
                builder.GetUpdateScopeInfoCommand(connection, null),
                builder.GetInsertScopeInfoClientCommand(connection, null),
                builder.GetUpdateScopeInfoClientCommand(connection, null),
            };

            foreach (var command in commands)
            {
                foreach (DbParameter parameter in command.Parameters)
                {
                    object representative = parameter.DbType switch
                    {
                        DbType.String => "{ \"name\": \"a json payload, not base64\" }",
                        DbType.Int64 => 123456789L,
                        DbType.DateTime => new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc),
                        _ => null,
                    };

                    // a null here means the parameter reports a DbType outside the safe set
                    // (e.g. Object from OracleDbType.Clob, Binary from Raw) — exactly the
                    // regression this test guards against.
                    Assert.True(representative is not null, $"{command.CommandText.Substring(0, 40)}... parameter {parameter.ParameterName} reports unsafe DbType {parameter.DbType}");

                    var converted = SyncTypeConverter.TryConvertFromDbType(representative, parameter.DbType);
                    Assert.Equal(representative, converted);
                }
            }
        }

        [Fact]
        public void GuidToRaw_SqlSubstrPositions_ReproduceGuidToByteArray()
        {
            // Extract the SUBSTR positions from the actual generated SQL and simulate the
            // expression in C#: the produced hex must equal Guid.ToByteArray(). A silent
            // transposition here would corrupt every scope id without raising any error.
            using var connection = new OracleConnection();
            var builder = new OracleScopeBuilder("scope_info");
            var text = builder.GetScopeInfoClientCommand(connection, null).CommandText;

            var substrs = Regex.Matches(text, @"SUBSTR\(REPLACE\(:sync_scope_id, '-', ''\),(\d+),(\d+)\)");
            Assert.Equal(9, substrs.Count);

            var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
            var h = guid.ToString("N"); // 32 hex chars, no dashes — same as REPLACE(:guid,'-','')

            var rawHex = string.Concat(substrs
                .Cast<Match>()
                .Select(m => h.Substring(int.Parse(m.Groups[1].Value) - 1, int.Parse(m.Groups[2].Value)))); // SQL SUBSTR is 1-based

            var expectedHex = Convert.ToHexString(guid.ToByteArray());
            Assert.Equal(expectedHex, rawHex, ignoreCase: true);
        }

        [Fact]
        public void ScopeCommands_PromoteLargeStringValuesToClobAtExecuteTime()
        {
            using var inner = new OracleCommand();
            var small = new OracleParameter { ParameterName = ":small", DbType = DbType.String, Value = "small json" };
            var large = new OracleParameter { ParameterName = ":large", DbType = DbType.String, Value = new string('x', 50_000) };
            inner.Parameters.Add(small);
            inner.Parameters.Add(large);

            OracleScopeCommand.PromoteLargeStringsToClob(inner.Parameters);

            Assert.Equal(OracleDbType.Varchar2, small.OracleDbType);
            Assert.Equal(OracleDbType.Clob, large.OracleDbType);
            Assert.Equal(new string('x', 50_000), large.Value);
        }
    }
}
