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

        [Theory]
        [InlineData("RAW", 16, typeof(System.Guid))]    // RAW(16) is how this provider stores GUIDs
        [InlineData("RAW", 32, typeof(byte[]))]
        [InlineData("RAW", 0, typeof(byte[]))]
        [InlineData("BLOB", 0, typeof(byte[]))]
        [InlineData("VARCHAR2", 0, typeof(string))]
        [InlineData("NUMBER", 0, typeof(decimal))]
        public void GetManagedType_MapsRaw16ToGuid(string oracleType, int dataLength, System.Type expected)
            => Assert.Equal(expected, Dotmim.Sync.Oracle.Builders.OracleTableBuilder.GetManagedType(oracleType, 0, 0, dataLength));
    }
}
