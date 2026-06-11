using Dotmim.Sync.Oracle;
using Microsoft.Data.SqlClient;
using Oracle.ManagedDataAccess.Client;
using System.Data.Common;
using Xunit;

namespace Dotmim.Sync.Tests.UnitTests.Oracle
{
    public class OracleCommandExtensionsTests
    {
        [Fact]
        public void EnsureBindByName_TurnsOnNameBasedBinding_OnOracleCommands()
        {
            // ODP.NET defaults to POSITIONAL binding: parameters are applied to placeholders in
            // add-order, not by name. Multi-parameter builder commands whose add-order differs
            // from the SQL placeholder order (e.g. the relation-columns lookup in
            // OracleTableBuilder.GetRelationsAsync) silently bind swapped values, which produced
            // inverted foreign keys when re-creating the schema on a remote provider.
            using var command = new OracleCommand();
            Assert.False(command.BindByName);

            var result = ((DbCommand)command).EnsureBindByName();

            Assert.Same(command, result);
            Assert.True(command.BindByName);
        }

        [Fact]
        public void EnsureBindByName_LeavesNonOracleCommandsUntouched()
        {
            using var command = new SqlCommand();

            var result = ((DbCommand)command).EnsureBindByName();

            Assert.Same(command, result);
        }
    }
}
