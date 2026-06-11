using Dotmim.Sync.Sqlite;
using Dotmim.Sync.Tests.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Oracle.ManagedDataAccess.Client;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Dotmim.Sync.SampleConsole
{
    public static class DBHelper
    {
        private static IConfiguration configuration;

        static DBHelper()
        {
            configuration = new ConfigurationBuilder()
              .AddJsonFile("appsettings.json", false, true)
              .AddJsonFile("appsettings.local.json", true, true)
              .Build();

        }

        public static string GetRandomName(string pref = default)
        {
            var str1 = Path.GetRandomFileName().Replace(".", "").ToLowerInvariant();
            return $"{pref}{str1}";
        }


        public static string GetConnectionString(string connectionStringName) =>
            configuration.GetSection("ConnectionStrings")[connectionStringName];

        public static string GetDatabaseConnectionString(string dbName) =>
            string.Format(configuration.GetSection("ConnectionStrings")["SqlConnection"], dbName);

        public static string GetAzureDatabaseConnectionString(string dbName) =>
            string.Format(configuration.GetSection("ConnectionStrings")["AzureSqlConnection"], dbName);

        public static string GetMySqlDatabaseConnectionString(string dbName) =>
            string.Format(configuration.GetSection("ConnectionStrings")["MySqlConnection"], dbName);

        public static string GetMariadbDatabaseConnectionString(string dbName) =>
            string.Format(configuration.GetSection("ConnectionStrings")["MariadbConnection"], dbName);


        public static string GetNpgsqlDatabaseConnectionString(string dbName) =>
            string.Format(configuration.GetSection("ConnectionStrings")["NpgsqlConnection"], dbName);

        // In Oracle a "database" is a user/schema, so dbName maps to the Oracle user name
        public static string GetOracleDatabaseConnectionString(string dbName) =>
            string.Format(configuration.GetSection("ConnectionStrings")["OracleConnection"], dbName);

        /// <summary>
        /// In Oracle a "database" is a user/schema, and creating one is an admin operation
        /// (the sync provider's EnsureDatabase is a no-op by design). Creates the user with
        /// CONNECT/RESOURCE grants and unlimited quota, using the OracleAdminConnection
        /// from appsettings.json. The password is the one used by the OracleConnection template.
        /// </summary>
        public static async Task CreateOracleDatabaseAsync(string dbName, bool recreateDb = false)
        {
            using var adminConnection = new OracleConnection(GetConnectionString("OracleAdminConnection"));
            await adminConnection.OpenAsync();

            if (recreateDb)
            {
                using var dropCommand = adminConnection.CreateCommand();
                dropCommand.CommandText = $"DROP USER {dbName} CASCADE";
                try { await dropCommand.ExecuteNonQueryAsync(); } catch (OracleException) { /* user may not exist */ }
            }

            using var existsCommand = adminConnection.CreateCommand();
            existsCommand.CommandText = "SELECT COUNT(*) FROM ALL_USERS WHERE USERNAME = UPPER(:userName)";
            var userNameParameter = existsCommand.CreateParameter();
            userNameParameter.ParameterName = ":userName";
            userNameParameter.Value = dbName;
            existsCommand.Parameters.Add(userNameParameter);

            if (Convert.ToInt32(await existsCommand.ExecuteScalarAsync()) > 0)
                return;

            foreach (var commandText in new[]
            {
                $"CREATE USER {dbName} IDENTIFIED BY \"Password12!\"",
                $"GRANT CONNECT, RESOURCE TO {dbName}",
                $"ALTER USER {dbName} QUOTA UNLIMITED ON USERS",
            })
            {
                using var command = adminConnection.CreateCommand();
                command.CommandText = commandText;
                await command.ExecuteNonQueryAsync();
            }
        }

        /// <summary>
        /// Creates the Oracle user if needed, then creates and seeds the AdventureWorks-style
        /// tables the sample uses. Oracle as SERVER needs its tables to already exist with
        /// data — sync only creates tables on the CLIENT side, from the server schema.
        /// Idempotent: tables that already exist are left untouched.
        /// </summary>
        public static async Task EnsureOracleAdventureWorksAsync(string dbName)
        {
            await CreateOracleDatabaseAsync(dbName);

            using var connection = new OracleConnection(GetOracleDatabaseConnectionString(dbName));
            await connection.OpenAsync();

            foreach (var (tableName, statements) in GetOracleAdventureWorksScripts())
            {
                using var existsCommand = connection.CreateCommand();
                existsCommand.CommandText = "SELECT COUNT(*) FROM USER_TABLES WHERE TABLE_NAME = :tableName";
                var tableNameParameter = existsCommand.CreateParameter();
                tableNameParameter.ParameterName = ":tableName";
                tableNameParameter.Value = tableName;
                existsCommand.Parameters.Add(tableNameParameter);

                if (Convert.ToInt32(await existsCommand.ExecuteScalarAsync()) > 0)
                    continue;

                foreach (var statement in statements)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    await command.ExecuteNonQueryAsync();
                }
            }
        }

        /// <summary>
        /// DDL + seed statements per table, in FK-consistent order. Guids are stored as
        /// RAW(16) in Guid.ToByteArray() order (HEXTORAW of the hex below), booleans as
        /// NUMBER(1), large text as NCLOB, photos as BLOB — the type mapping documented
        /// in docs/Oracle.md.
        /// </summary>
        private static IEnumerable<(string TableName, string[] Statements)> GetOracleAdventureWorksScripts()
        {
            static string NewRawGuid() => Convert.ToHexString(Guid.NewGuid().ToByteArray());

            string product1 = NewRawGuid(), product2 = NewRawGuid(), product3 = NewRawGuid();
            string customer1 = NewRawGuid(), customer2 = NewRawGuid();

            yield return ("ProductCategory", new[]
            {
                """
                CREATE TABLE "ProductCategory" (
                  "ProductCategoryID" NVARCHAR2(12) NOT NULL,
                  "ParentProductCategoryID" NVARCHAR2(12) NULL,
                  "Name" NVARCHAR2(50) NOT NULL,
                  "rowguid" RAW(16) NULL,
                  "ModifiedDate" TIMESTAMP NULL,
                  CONSTRAINT "PK_ProductCategory" PRIMARY KEY ("ProductCategoryID"))
                """,
                $"""INSERT INTO "ProductCategory" ("ProductCategoryID", "Name", "rowguid", "ModifiedDate") VALUES ('A_BIKES', 'Bikes', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "ProductCategory" ("ProductCategoryID", "ParentProductCategoryID", "Name", "rowguid", "ModifiedDate") VALUES ('MOUNTB', 'A_BIKES', 'Mountain Bikes', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "ProductCategory" ("ProductCategoryID", "ParentProductCategoryID", "Name", "rowguid", "ModifiedDate") VALUES ('ROADB', 'A_BIKES', 'Road Bikes', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "ProductCategory" ("ProductCategoryID", "Name", "rowguid", "ModifiedDate") VALUES ('ROADFR', 'Road Frames', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "ProductCategory" ("ProductCategoryID", "Name", "rowguid", "ModifiedDate") VALUES ('HANDLB', 'Handlebars', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
            });

            yield return ("ProductModel", new[]
            {
                """
                CREATE TABLE "ProductModel" (
                  "ProductModelID" NUMBER(10) NOT NULL,
                  "Name" NVARCHAR2(50) NOT NULL,
                  "CatalogDescription" NCLOB NULL,
                  "rowguid" RAW(16) NULL,
                  "ModifiedDate" TIMESTAMP NULL,
                  CONSTRAINT "PK_ProductModel" PRIMARY KEY ("ProductModelID"))
                """,
                $"""INSERT INTO "ProductModel" ("ProductModelID", "Name", "rowguid", "ModifiedDate") VALUES (6, 'HL Road Frame', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "ProductModel" ("ProductModelID", "Name", "rowguid", "ModifiedDate") VALUES (19, 'Mountain-100', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
            });

            yield return ("Product", new[]
            {
                """
                CREATE TABLE "Product" (
                  "ProductID" RAW(16) NOT NULL,
                  "Name" NVARCHAR2(50) NOT NULL,
                  "ProductNumber" NVARCHAR2(25) NOT NULL,
                  "Color" NVARCHAR2(15) NULL,
                  "StandardCost" NUMBER(19,4) NOT NULL,
                  "ListPrice" NUMBER(19,4) NOT NULL,
                  "Size" NVARCHAR2(5) NULL,
                  "Weight" NUMBER(8,2) NULL,
                  "ProductCategoryID" NVARCHAR2(12) NULL,
                  "ProductModelID" NUMBER(10) NULL,
                  "SellStartDate" TIMESTAMP NULL,
                  "SellEndDate" TIMESTAMP NULL,
                  "DiscontinuedDate" TIMESTAMP NULL,
                  "ThumbNailPhoto" BLOB NULL,
                  "ThumbnailPhotoFileName" NVARCHAR2(50) NULL,
                  "rowguid" RAW(16) NULL,
                  "ModifiedDate" TIMESTAMP NULL,
                  CONSTRAINT "PK_Product" PRIMARY KEY ("ProductID"))
                """,
                $"""INSERT INTO "Product" ("ProductID", "Name", "ProductNumber", "Color", "StandardCost", "ListPrice", "Size", "ProductCategoryID", "ProductModelID", "SellStartDate", "rowguid", "ModifiedDate") VALUES (HEXTORAW('{product1}'), 'HL Road Frame - Red, 58', 'FR-R92R-58', 'Red', 1059.31, 1431.50, '58', 'ROADFR', 6, SYSTIMESTAMP, HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "Product" ("ProductID", "Name", "ProductNumber", "Color", "StandardCost", "ListPrice", "Size", "ProductCategoryID", "ProductModelID", "SellStartDate", "rowguid", "ModifiedDate") VALUES (HEXTORAW('{product2}'), 'Mountain-100 Silver, 38', 'BK-M82S-38', 'Silver', 1912.15, 3399.99, '38', 'MOUNTB', 19, SYSTIMESTAMP, HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "Product" ("ProductID", "Name", "ProductNumber", "Color", "StandardCost", "ListPrice", "Size", "ProductCategoryID", "ProductModelID", "SellStartDate", "rowguid", "ModifiedDate") VALUES (HEXTORAW('{product3}'), 'Road-150 Red, 62', 'BK-R93R-62', 'Red', 2171.29, 3578.27, '62', 'ROADB', 19, SYSTIMESTAMP, HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
            });

            yield return ("Address", new[]
            {
                """
                CREATE TABLE "Address" (
                  "AddressID" NUMBER(10) NOT NULL,
                  "AddressLine1" NVARCHAR2(60) NOT NULL,
                  "AddressLine2" NVARCHAR2(60) NULL,
                  "City" NVARCHAR2(30) NOT NULL,
                  "StateProvince" NVARCHAR2(50) NOT NULL,
                  "CountryRegion" NVARCHAR2(50) NOT NULL,
                  "PostalCode" NVARCHAR2(15) NOT NULL,
                  "rowguid" RAW(16) NULL,
                  "ModifiedDate" TIMESTAMP NULL,
                  CONSTRAINT "PK_Address" PRIMARY KEY ("AddressID"))
                """,
                $"""INSERT INTO "Address" ("AddressID", "AddressLine1", "City", "StateProvince", "CountryRegion", "PostalCode", "rowguid", "ModifiedDate") VALUES (1, '8713 Yosemite Ct.', 'Bothell', 'Washington', 'United States', '98011', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "Address" ("AddressID", "AddressLine1", "City", "StateProvince", "CountryRegion", "PostalCode", "rowguid", "ModifiedDate") VALUES (2, '1318 Lasalle Street', 'Bothell', 'Washington', 'United States', '98011', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
            });

            yield return ("Customer", new[]
            {
                """
                CREATE TABLE "Customer" (
                  "CustomerID" RAW(16) NOT NULL,
                  "EmployeeID" NUMBER(10) NULL,
                  "NameStyle" NUMBER(1) NOT NULL,
                  "Title" NVARCHAR2(8) NULL,
                  "FirstName" NVARCHAR2(50) NOT NULL,
                  "MiddleName" NVARCHAR2(50) NULL,
                  "LastName" NVARCHAR2(50) NOT NULL,
                  "Suffix" NVARCHAR2(10) NULL,
                  "CompanyName" NVARCHAR2(128) NULL,
                  "SalesPerson" NVARCHAR2(256) NULL,
                  "EmailAddress" NVARCHAR2(50) NULL,
                  "Phone" NVARCHAR2(25) NULL,
                  "PasswordHash" NVARCHAR2(128) NULL,
                  "PasswordSalt" NVARCHAR2(10) NULL,
                  "rowguid" RAW(16) NULL,
                  "ModifiedDate" TIMESTAMP NULL,
                  CONSTRAINT "PK_Customer" PRIMARY KEY ("CustomerID"))
                """,
                $"""INSERT INTO "Customer" ("CustomerID", "NameStyle", "Title", "FirstName", "LastName", "CompanyName", "EmailAddress", "Phone", "rowguid", "ModifiedDate") VALUES (HEXTORAW('{customer1}'), 0, 'Mr.', 'Orlando', 'Gee', 'A Bike Store', 'orlando0@adventure-works.com', '245-555-0173', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "Customer" ("CustomerID", "NameStyle", "Title", "FirstName", "LastName", "CompanyName", "EmailAddress", "Phone", "rowguid", "ModifiedDate") VALUES (HEXTORAW('{customer2}'), 0, 'Ms.', 'Keith', 'Harris', 'Progressive Sports', 'keith0@adventure-works.com', '170-555-0127', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
            });

            yield return ("CustomerAddress", new[]
            {
                """
                CREATE TABLE "CustomerAddress" (
                  "CustomerID" RAW(16) NOT NULL,
                  "AddressID" NUMBER(10) NOT NULL,
                  "AddressType" NVARCHAR2(50) NOT NULL,
                  "rowguid" RAW(16) NULL,
                  "ModifiedDate" TIMESTAMP NULL,
                  CONSTRAINT "PK_CustomerAddress" PRIMARY KEY ("CustomerID", "AddressID"))
                """,
                $"""INSERT INTO "CustomerAddress" ("CustomerID", "AddressID", "AddressType", "rowguid", "ModifiedDate") VALUES (HEXTORAW('{customer1}'), 1, 'Main Office', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "CustomerAddress" ("CustomerID", "AddressID", "AddressType", "rowguid", "ModifiedDate") VALUES (HEXTORAW('{customer2}'), 2, 'Main Office', HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
            });

            yield return ("SalesOrderHeader", new[]
            {
                """
                CREATE TABLE "SalesOrderHeader" (
                  "SalesOrderID" NUMBER(10) NOT NULL,
                  "RevisionNumber" NUMBER(3) NOT NULL,
                  "OrderDate" TIMESTAMP NULL,
                  "DueDate" TIMESTAMP NULL,
                  "ShipDate" TIMESTAMP NULL,
                  "Status" NUMBER(3) NOT NULL,
                  "OnlineOrderFlag" NUMBER(1) NULL,
                  "SalesOrderNumber" NVARCHAR2(25) NULL,
                  "PurchaseOrderNumber" NVARCHAR2(25) NULL,
                  "AccountNumber" NVARCHAR2(15) NULL,
                  "CustomerID" RAW(16) NOT NULL,
                  "ShipToAddressID" NUMBER(10) NULL,
                  "BillToAddressID" NUMBER(10) NULL,
                  "ShipMethod" NVARCHAR2(50) NULL,
                  "CreditCardApprovalCode" NVARCHAR2(15) NULL,
                  "SubTotal" NUMBER(19,4) NOT NULL,
                  "TaxAmt" NUMBER(19,4) NOT NULL,
                  "Freight" NUMBER(19,4) NOT NULL,
                  "TotalDue" NUMBER(19,4) NOT NULL,
                  "Comment" NVARCHAR2(1000) NULL,
                  "rowguid" RAW(16) NULL,
                  "ModifiedDate" TIMESTAMP NULL,
                  CONSTRAINT "PK_SalesOrderHeader" PRIMARY KEY ("SalesOrderID"))
                """,
                $"""INSERT INTO "SalesOrderHeader" ("SalesOrderID", "RevisionNumber", "OrderDate", "DueDate", "Status", "OnlineOrderFlag", "SalesOrderNumber", "AccountNumber", "CustomerID", "ShipToAddressID", "BillToAddressID", "ShipMethod", "SubTotal", "TaxAmt", "Freight", "TotalDue", "rowguid", "ModifiedDate") VALUES (71774, 2, SYSTIMESTAMP, SYSTIMESTAMP + 7, 5, 1, 'SO71774', '10-4020-000609', HEXTORAW('{customer1}'), 1, 1, 'CARGO TRANSPORT 5', 880.35, 70.43, 22.01, 972.79, HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
            });

            yield return ("SalesOrderDetail", new[]
            {
                """
                CREATE TABLE "SalesOrderDetail" (
                  "SalesOrderID" NUMBER(10) NOT NULL,
                  "SalesOrderDetailID" NUMBER(10) NOT NULL,
                  "OrderQty" NUMBER(5) NOT NULL,
                  "ProductID" RAW(16) NOT NULL,
                  "UnitPrice" NUMBER(19,4) NOT NULL,
                  "UnitPriceDiscount" NUMBER(19,4) NOT NULL,
                  "LineTotal" NUMBER(19,6) NULL,
                  "rowguid" RAW(16) NULL,
                  "ModifiedDate" TIMESTAMP NULL,
                  CONSTRAINT "PK_SalesOrderDetail" PRIMARY KEY ("SalesOrderID", "SalesOrderDetailID"))
                """,
                $"""INSERT INTO "SalesOrderDetail" ("SalesOrderID", "SalesOrderDetailID", "OrderQty", "ProductID", "UnitPrice", "UnitPriceDiscount", "LineTotal", "rowguid", "ModifiedDate") VALUES (71774, 110562, 1, HEXTORAW('{product1}'), 356.90, 0, 356.90, HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
                $"""INSERT INTO "SalesOrderDetail" ("SalesOrderID", "SalesOrderDetailID", "OrderQty", "ProductID", "UnitPrice", "UnitPriceDiscount", "LineTotal", "rowguid", "ModifiedDate") VALUES (71774, 110563, 2, HEXTORAW('{product2}'), 1391.99, 0, 2783.98, HEXTORAW('{NewRawGuid()}'), SYSTIMESTAMP)""",
            });
        }



        /// <summary>
        /// create a server database with datas and an empty client database
        /// </summary>
        
        public static async Task EnsureDatabasesAsync(string databaseName, bool useSeeding = true)
        {
            // Create server database with items
            using var dbServer = new AdventureWorksContext(GetDatabaseConnectionString(databaseName), useSeeding);
            await dbServer.Database.EnsureDeletedAsync();
            await dbServer.Database.EnsureCreatedAsync();
        }

        public static async Task DeleteDatabaseAsync(string dbName)
        {
            var masterConnection = new SqlConnection(GetDatabaseConnectionString("master"));
            await masterConnection.OpenAsync();
            var cmdDb = new SqlCommand(GetDeleteDatabaseScript(dbName), masterConnection);
            await cmdDb.ExecuteNonQueryAsync();
            masterConnection.Close();
        }



        public static async Task CreateDatabaseAsync(string dbName, bool recreateDb = true)
        {
            var masterConnection = new SqlConnection(GetDatabaseConnectionString("master"));
            await masterConnection.OpenAsync();
            var cmdDb = new SqlCommand(GetCreationDBScript(dbName, recreateDb), masterConnection);
            await cmdDb.ExecuteNonQueryAsync();
            masterConnection.Close();
        }

        private static string GetDeleteDatabaseScript(string dbName) =>
                  $@"if (exists (Select * from sys.databases where name = '{dbName}'))
            begin
	            alter database [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE
	            drop database {dbName}
            end";

        private static string GetCreationDBScript(string dbName, bool recreateDb = true)
        {
            if (recreateDb)
                return $@"if (exists (Select * from sys.databases where name = '{dbName}'))
                    begin
	                    alter database [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE
	                    drop database {dbName}
                    end
                    Create database {dbName}";
            else
                return $@"if not (exists (Select * from sys.databases where name = '{dbName}')) 
                          Create database {dbName}";

        }

        public static async Task ExecuteSqliteScriptAsync(string connectionString, string commandText)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            using var cmdDb = new SqliteCommand(commandText, connection);
            await cmdDb.ExecuteNonQueryAsync();

            connection.Close();
        }

        public static async Task ExecuteScriptAsync(string dbName, string script)
        {
            using var connection = new SqlConnection(GetDatabaseConnectionString(dbName));
            connection.Open();

            //split the script on "GO" commands
            string[] splitter = new string[] { "\r\nGO\r\n" };
            string[] commandTexts = script.Split(splitter, StringSplitOptions.RemoveEmptyEntries);

            foreach (string commandText in commandTexts)
            {
                using var cmdDb = new SqlCommand(commandText, connection);
                await cmdDb.ExecuteNonQueryAsync();
            }
            connection.Close();
        }




        internal static async Task<Guid> AddProductCategoryRowAsync(
            CoreProvider provider, Guid? parentProductCategoryId = default, string name = default)
        {
            string commandText = $"Insert into ProductCategory (ProductCategoryId, ParentProductCategoryID, Name, ModifiedDate, rowguid) " +
                                 $"Values (@ProductCategoryId, @ParentProductCategoryID, @Name, @ModifiedDate, @rowguid)";

            var connection = provider.CreateConnection();

            connection.Open();

            var pId = Guid.NewGuid();

            var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Connection = connection;

            var p = command.CreateParameter();
            p.DbType = DbType.Guid;
            p.ParameterName = "@ProductCategoryId";
            p.Value = pId;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.Guid;
            p.ParameterName = "@ParentProductCategoryID";
            p.Value = parentProductCategoryId.HasValue  ?  parentProductCategoryId : DBNull.Value ;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.String;
            p.ParameterName = "@Name";
            p.Value = string.IsNullOrEmpty(name) ? Path.GetRandomFileName().Replace(".", "").ToLowerInvariant() + ' ' + Path.GetRandomFileName().Replace(".", "").ToLowerInvariant() : name;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.Guid;
            p.ParameterName = "@rowguid";
            p.Value = Guid.NewGuid();
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.DateTime;
            p.ParameterName = "@ModifiedDate";
            p.Value = DateTime.UtcNow;
            command.Parameters.Add(p);

            await command.ExecuteNonQueryAsync();

            connection.Close();

            return pId;
        }

        internal static async Task DeleteProductCategoryRowAsync(CoreProvider provider, Guid? productId = default, string name = default)
        {
            string commandText = $"Delete From ProductCategory Where " +
                                 $"(ProductCategoryId = @ProductCategoryId And @ProductCategoryId is not null) OR " +
                                 $"(Name = @Name And @Name is not null)";

            var connection = provider.CreateConnection();

            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Connection = connection;

            var p = command.CreateParameter();
            p.DbType = DbType.Guid;
            p.ParameterName = "@ProductCategoryId";
            p.Value = productId.HasValue ? productId.Value : DBNull.Value;
            command.Parameters.Add(p);


            p = command.CreateParameter();
            p.DbType = DbType.String;
            p.ParameterName = "@Name";
            p.Value = string.IsNullOrEmpty(name) ? DBNull.Value : name;
            command.Parameters.Add(p);

            await command.ExecuteNonQueryAsync();

            connection.Close();
        }

        private static async Task AddProductRowAsync(CoreProvider provider)
        {

            string commandText = "Insert into Product (Name, ProductNumber, StandardCost, ListPrice, SellStartDate) Values (@Name, @ProductNumber, @StandardCost, @ListPrice, @SellStartDate)";
            var connection = provider.CreateConnection();

            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Connection = connection;

            var p = command.CreateParameter();
            p.DbType = DbType.String;
            p.ParameterName = "@Name";
            p.Value = Path.GetRandomFileName().Replace(".", "").ToLowerInvariant();
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.String;
            p.ParameterName = "@ProductNumber";
            p.Value = Path.GetRandomFileName().Replace(".", "").ToLowerInvariant().Substring(0, 6).ToUpperInvariant();
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.Double;
            p.ParameterName = "@StandardCost";
            p.Value = 100;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.Double;
            p.ParameterName = "@ListPrice";
            p.Value = 100;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.DateTime;
            p.ParameterName = "@SellStartDate";
            p.Value = DateTime.UtcNow;
            command.Parameters.Add(p);

            await command.ExecuteNonQueryAsync();

            connection.Close();

        }
        private static async Task AddProductCategoryRowWithOneMoreColumnAsync(CoreProvider provider)
        {

            string commandText = "Insert into ProductCategory (ProductCategoryId, Name, ModifiedDate, CreatedDate, rowguid) Values (@ProductCategoryId, @Name, @ModifiedDate, @CreatedDate, @rowguid)";
            var connection = provider.CreateConnection();

            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Connection = connection;

            var p = command.CreateParameter();
            p.DbType = DbType.String;
            p.ParameterName = "@Name";
            p.Value = Path.GetRandomFileName().Replace(".", "").ToLowerInvariant();
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.Guid;
            p.ParameterName = "@ProductCategoryId";
            p.Value = Guid.NewGuid();
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.DateTime;
            p.ParameterName = "@ModifiedDate";
            p.Value = DateTime.UtcNow;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.DateTime;
            p.ParameterName = "@CreatedDate";
            p.Value = DateTime.UtcNow;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.DbType = DbType.Guid;
            p.ParameterName = "@rowguid";
            p.Value = Guid.NewGuid();
            command.Parameters.Add(p);

            await command.ExecuteNonQueryAsync();

            connection.Close();

        }


        private static async Task AddColumnsToProductCategoryAsync(CoreProvider provider)
        {
            var commandText = @"ALTER TABLE dbo.ProductCategory ADD CreatedDate datetime NULL;";

            var connection = provider.CreateConnection();

            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Connection = connection;

            await command.ExecuteNonQueryAsync();

            connection.Close();
        }
        private static async Task UpdateAllProductCategoryAsync(CoreProvider provider, string addedString)
        {
            string commandText = "Update ProductCategory Set Name = Name + @addedString";
            var connection = provider.CreateConnection();

            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.Connection = connection;

            var p = command.CreateParameter();
            p.DbType = DbType.String;
            p.ParameterName = "@addedString";
            p.Value = addedString;
            command.Parameters.Add(p);

            await command.ExecuteNonQueryAsync();

            connection.Close();
        }

    }
}