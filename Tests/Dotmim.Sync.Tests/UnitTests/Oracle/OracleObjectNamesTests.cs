using Dotmim.Sync.Builders;
using Dotmim.Sync.Oracle.Builders;
using System;
using System.Data;
using System.Text.RegularExpressions;
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

        [Fact]
        public void SelectChangesWithFilters_GroupsWheresWithTombstoneEscape_BeforeTimestampGuard()
        {
            var objectNames = BuildObjectNames();

            var filter = new SyncFilter("Product");
            filter.Parameters.Add(new SyncFilterParameter { Name = "ProductCategoryId", DbType = DbType.Guid });
            filter.Wheres.Add(new SyncFilterWhereSideItem { TableName = "Product", ColumnName = "ProductCategoryId", ParameterName = "ProductCategoryId" });

            var sql = objectNames.GetCommandText(DbCommandType.SelectChangesWithFilters, filter);

            // the tombstone escape must be CLOSED by a paren before the timestamp guard's AND:
            // ((wheres) OR tombstone) AND ts ... — without the close, AND binds tighter and
            // the timestamp/scope guards are bypassed for every filtered row.
            Assert.Matches(@"OR side\.""sync_row_is_tombstone"" = 1\s*\)", sql);

            // and the timestamp guard must come after that closing paren
            var tombstoneClose = Regex.Match(sql, @"OR side\.""sync_row_is_tombstone"" = 1\s*\)").Index;
            var timestampGuard = sql.IndexOf("side.\"timestamp\" > :sync_min_timestamp", StringComparison.Ordinal);
            Assert.True(tombstoneClose < timestampGuard, $"guard ordering wrong:\n{sql}");
        }

        [Fact]
        public void SelectChangesWithFilters_CustomWhereTemplate_KeepsTableAliasesUnquoted()
        {
            var objectNames = BuildObjectNames();

            var filter = new SyncFilter("Product");
            filter.CustomWheres.Add("{{{ProductCategoryId}}} IS NOT NULL OR {{{side}}}.{{{sync_row_is_tombstone}}} = 1");

            var sql = objectNames.GetCommandText(DbCommandType.SelectChangesWithFilters, filter);

            // the {{{...}}} template quotes every identifier, including the {{{side}}}/{{{base}}}
            // aliases — but the FROM clause declares those aliases UNQUOTED and Oracle folds
            // unquoted identifiers to upper case, so a quoted lower-case "side" would not
            // resolve (ORA-00904). The alias references must be folded back to unquoted form.
            Assert.Contains("side.\"sync_row_is_tombstone\" = 1", sql);
            Assert.DoesNotContain("\"side\".", sql);
            Assert.DoesNotContain("\"base\".", sql);

            // regular identifiers keep their provider quoting
            Assert.Contains("\"ProductCategoryId\" IS NOT NULL", sql);
        }

        [Fact]
        public void TimestampValue_EvaluatesSystimestampExactlyOnce()
        {
            // seconds and fractional parts must come from ONE reading; two evaluations can
            // straddle a second boundary and skew the clock by up to a second
            var occurrences = Regex.Matches(OracleObjectNames.TimestampValue, "SYSTIMESTAMP").Count;
            Assert.Equal(1, occurrences);
        }

        [Fact]
        public void EnableConstraints_UsesNovalidate()
        {
            var sql = BuildObjectNames().GetCommandText(DbCommandType.EnableConstraints);
            Assert.Contains("ENABLE NOVALIDATE CONSTRAINT", sql);
            Assert.DoesNotContain(" ENABLE CONSTRAINT ", sql);
        }

        [Fact]
        public void ConstraintCommands_EmbedTableNameAsLiteral()
        {
            var sql = BuildObjectNames().GetCommandText(DbCommandType.DisableConstraints);
            Assert.Contains("= 'Product'", sql);
        }

        [Fact]
        public void ConstraintCommands_EscapeApostrophesInTableNameLiteral()
        {
            var table = new SyncTable("O'Brien");
            table.Columns.Add(new SyncColumn("Id", typeof(int)));
            table.PrimaryKeys.Add("Id");

            var schema = new SyncSet();
            schema.Tables.Add(table);

            var scopeInfo = new ScopeInfo { Name = "DefaultScope", Setup = new SyncSetup("O'Brien") };
            var objectNames = new OracleObjectNames(table, scopeInfo);

            var sql = objectNames.GetCommandText(DbCommandType.DisableConstraints);

            Assert.Contains("= 'O''Brien'", sql);
        }

        [Fact]
        public void CreateTrackingTableScript_CreatesTableAndTimestampIndex()
        {
            var script = BuildObjectNames().CreateTrackingTableScript(_ => "RAW(16)");

            // two DDL statements wrapped in one PL/SQL block (Oracle cannot batch DDL)
            Assert.Contains("EXECUTE IMMEDIATE 'CREATE TABLE", script);
            Assert.Contains("EXECUTE IMMEDIATE 'CREATE INDEX", script);
            Assert.Contains("CREATE INDEX \"", script); // index name must be double-quoted
            Assert.Contains("(\"timestamp\")", script);
            Assert.StartsWith("BEGIN", script.TrimStart());
        }

        [Fact]
        public void GetTriggerCommandName_ThrowsWhenIdentifierExceeds128Bytes()
        {
            var table = new SyncTable("Product");
            table.Columns.Add(new SyncColumn("Id", typeof(int)));
            table.PrimaryKeys.Add("Id");

            var schema = new SyncSet();
            schema.Tables.Add(table);

            // tracking name ("Product_tracking") is fine; the trigger name
            // "Product" + 120 chars + "_insert_trigger" = 142 bytes > 128
            var setup = new SyncSetup("Product") { TriggersSuffix = new string('X', 120) };
            var scopeInfo = new ScopeInfo { Name = "DefaultScope", Setup = setup };
            var objectNames = new OracleObjectNames(table, scopeInfo);

            Assert.Throws<ArgumentException>(() => objectNames.GetTriggerCommandName(DbTriggerType.Insert));
        }

        [Fact]
        public void Constructor_ThrowsWhenTrackingTableNameExceeds128Bytes()
        {
            var longName = new string('X', 125); // + "_tracking" => 134 chars > 128
            var table = new SyncTable(longName);
            table.Columns.Add(new SyncColumn("Id", typeof(int)));
            table.PrimaryKeys.Add("Id");

            var schema = new SyncSet();
            schema.Tables.Add(table);

            var scopeInfo = new ScopeInfo { Name = "DefaultScope", Setup = new SyncSetup(longName) };

            Assert.Throws<ArgumentException>(() => new OracleObjectNames(table, scopeInfo));
        }
    }
}
