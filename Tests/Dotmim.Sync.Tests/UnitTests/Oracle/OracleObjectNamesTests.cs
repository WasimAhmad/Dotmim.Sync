using Dotmim.Sync.Builders;
using Dotmim.Sync.Oracle.Builders;
using System;
using Xunit;

namespace Dotmim.Sync.Tests.UnitTests.Oracle
{
    public class OracleObjectNamesTests
    {
        internal static OracleObjectNames BuildObjectNames()
        {
            var table = new SyncTable("Product");
            table.Columns.Add(new SyncColumn("ProductId", typeof(Guid)));
            table.Columns.Add(new SyncColumn("ProductCategoryId", typeof(Guid)));
            table.Columns.Add(new SyncColumn("Name", typeof(string)));
            table.PrimaryKeys.Add("ProductId");

            var schema = new SyncSet();
            schema.Tables.Add(table);

            var scopeInfo = new ScopeInfo { Name = "DefaultScope", Setup = new SyncSetup("Product") };
            return new OracleObjectNames(table, scopeInfo);
        }

        private static SyncFilter BuildJoinFilter()
        {
            var filter = new SyncFilter("Product");
            filter.Joins.Add(new SyncFilterJoin
            {
                JoinEnum = Join.Inner,
                TableName = "ProductCategory",
                LeftTableName = "Product",
                LeftColumnName = "ProductCategoryId",
                RightTableName = "ProductCategory",
                RightColumnName = "ProductCategoryId",
            });
            return filter;
        }

        [Fact]
        public void CreateFilterCustomJoins_EmitsJoinClause_AliasingFilterTableAsBase()
        {
            var objectNames = BuildObjectNames();

            var sql = objectNames.CreateFilterCustomJoins(BuildJoinFilter());

            Assert.Contains("INNER JOIN \"ProductCategory\"", sql);
            Assert.Contains("base.\"ProductCategoryId\" = \"ProductCategory\".\"ProductCategoryId\"", sql);
        }

        [Fact]
        public void CreateFilterCustomJoins_MapsOuterToFullOuterJoin()
        {
            var objectNames = BuildObjectNames();
            var filter = new SyncFilter("Product");
            filter.Joins.Add(new SyncFilterJoin
            {
                JoinEnum = Join.Outer,
                TableName = "ProductCategory",
                LeftTableName = "Product",
                LeftColumnName = "ProductCategoryId",
                RightTableName = "ProductCategory",
                RightColumnName = "ProductCategoryId",
            });

            var sql = objectNames.CreateFilterCustomJoins(filter);

            // plain "OUTER JOIN" is invalid Oracle SQL
            Assert.Contains("FULL OUTER JOIN \"ProductCategory\"", sql);
        }

        [Fact]
        public void SelectChangesWithFilters_ContainsCustomJoins_BetweenTrackingJoinAndWhere()
        {
            var objectNames = BuildObjectNames();

            var sql = objectNames.GetCommandText(DbCommandType.SelectChangesWithFilters, BuildJoinFilter());

            Assert.Contains("INNER JOIN \"ProductCategory\"", sql);
            // the custom join must come after the tracking-table join and before the WHERE
            var trackingJoinIndex = sql.IndexOf("RIGHT JOIN", StringComparison.Ordinal);
            var customJoinIndex = sql.IndexOf("INNER JOIN \"ProductCategory\"", StringComparison.Ordinal);
            var whereIndex = sql.IndexOf("WHERE", StringComparison.Ordinal);
            Assert.True(trackingJoinIndex < customJoinIndex && customJoinIndex < whereIndex,
                $"join ordering wrong:\n{sql}");
        }

        [Fact]
        public void SelectInitializedChangesWithFilters_ContainsCustomJoins_OnlyInFirstBranch()
        {
            var objectNames = BuildObjectNames();

            var sql = objectNames.GetCommandText(DbCommandType.SelectInitializedChangesWithFilters, BuildJoinFilter());

            // the LEFT JOIN branch gets the custom joins; the tombstone UNION branch does not
            var unionIndex = sql.IndexOf("UNION", StringComparison.Ordinal);
            var customJoinIndex = sql.IndexOf("INNER JOIN \"ProductCategory\"", StringComparison.Ordinal);
            Assert.True(customJoinIndex >= 0, "custom join missing entirely");
            Assert.True(customJoinIndex < unionIndex, "custom join must be in the first branch");
            Assert.DoesNotContain("INNER JOIN \"ProductCategory\"", sql.Substring(unionIndex));
        }
    }
}
