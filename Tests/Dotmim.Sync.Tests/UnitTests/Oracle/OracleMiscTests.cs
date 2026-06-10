using Dotmim.Sync.Oracle;
using Xunit;

namespace Dotmim.Sync.Tests.UnitTests.Oracle
{
    public class OracleMiscTests
    {
        [Theory]
        [InlineData(60, true)]     // deadlock detected — transient
        [InlineData(2049, true)]   // distributed lock timeout — transient
        [InlineData(3113, true)]   // end-of-file on communication channel — transient
        [InlineData(942, false)]   // table or view does not exist — permanent
        [InlineData(1542, false)]  // tablespace offline — permanent (was wrongly retried)
        [InlineData(12154, false)] // cannot resolve connect identifier — config error, permanent
        public void IsTransient_ClassifiesOracleErrorNumbers(int number, bool expected)
            => Assert.Equal(expected, OracleTransientExceptionDetector.IsTransient(number));

        [Fact]
        public void GetDatabaseName_ReturnsUserSchema_NotDataSource()
        {
            var provider = new OracleSyncProvider("Data Source=localhost:1521/FREEPDB1;User Id=SCOTT;Password=x;");

            // In the Oracle model the "database" is the user/schema; DataSource is the host
            Assert.Equal("SCOTT", provider.GetDatabaseName());
        }
    }
}
