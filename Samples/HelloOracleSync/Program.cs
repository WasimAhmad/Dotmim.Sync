using Dotmim.Sync;
using Dotmim.Sync.Oracle;
using Dotmim.Sync.SqlServer;
using System;
using System.Threading.Tasks;

namespace HelloOracleSync
{
    internal class Program
    {
        // Server: SQL Server AdventureWorks. Client: an Oracle user/schema.
        // Oracle note: a "database" is a user/schema — create it first as an admin:
        //   CREATE USER DMS_SAMPLE IDENTIFIED BY "Password12!";
        //   GRANT CONNECT, RESOURCE TO DMS_SAMPLE;
        //   ALTER USER DMS_SAMPLE QUOTA UNLIMITED ON USERS;
        private const string ServerConnectionString =
            "Data Source=(localdb)\\mssqllocaldb;Initial Catalog=AdventureWorks;Integrated Security=true;";

        private const string ClientConnectionString =
            "Data Source=localhost:1521/FREEPDB1;User Id=DMS_SAMPLE;Password=Password12!;";

        private static async Task Main()
        {
            var serverProvider = new SqlSyncProvider(ServerConnectionString);
            var clientProvider = new OracleSyncProvider(ClientConnectionString);

            var setup = new SyncSetup("ProductCategory", "ProductModel", "Product");
            var agent = new SyncAgent(clientProvider, serverProvider);

            do
            {
                var result = await agent.SynchronizeAsync(setup).ConfigureAwait(false);
                Console.WriteLine(result);
                Console.WriteLine("Sync ended. Press a key to sync again, or Escape to exit.");
            }
            while (Console.ReadKey().Key != ConsoleKey.Escape);
        }
    }
}
