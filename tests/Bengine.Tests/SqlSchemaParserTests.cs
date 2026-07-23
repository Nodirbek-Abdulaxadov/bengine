namespace Bengine.Tests;

using System.Linq;
using Bengine.Enums;
using Bengine.Parsing;

public class SqlSchemaParserTests
{
    [Fact]
    public void Parses_columns_types_and_modifiers()
    {
        const string sql = """
            CREATE TABLE users (
                id BIGINT NOT NULL,
                email VARCHAR(320) NOT NULL,
                bio TEXT,
                balance DECIMAL(12,2) NOT NULL,
                created_at TIMESTAMP(6)
            );
            """;

        var table = SqlSchemaParser.ParseAs(sql, DatabaseProvider.PostgreSql).Tables.Single();

        Assert.Equal("users", table.Name);
        Assert.Collection(
            table.Columns,
            c => Assert.Equal((DbDataType.BigInt, Nullability.NotNull), (c.Type.DataType, c.Nullability)),
            c =>
            {
                Assert.Equal(DbDataType.VarChar, c.Type.DataType);
                Assert.Equal(320, c.Type.Length);
            },
            c => Assert.Equal((DbDataType.Text, Nullability.Nullable), (c.Type.DataType, c.Nullability)),
            c =>
            {
                Assert.Equal(DbDataType.Decimal, c.Type.DataType);
                Assert.Equal(12, c.Type.Precision);
                Assert.Equal(2, c.Type.Scale);
            },
            c =>
            {
                Assert.Equal(DbDataType.Timestamp, c.Type.DataType);
                Assert.Equal(6, c.Type.FractionalSecondsPrecision);
            });
    }

    [Fact]
    public void Column_is_nullable_when_unspecified_and_not_null_when_declared()
    {
        const string sql = "CREATE TABLE t (a INT, b INT NOT NULL, c INT PRIMARY KEY);";

        var columns = SqlSchemaParser.ParseAs(sql, DatabaseProvider.PostgreSql).Tables.Single().Columns;

        Assert.Equal(Nullability.Nullable, columns[0].Nullability);
        Assert.Equal(Nullability.NotNull, columns[1].Nullability);
        Assert.Equal(Nullability.NotNull, columns[2].Nullability);
    }

    [Fact]
    public void Parses_inline_and_table_level_primary_keys()
    {
        var inline = SqlSchemaParser.ParseAs(
            "CREATE TABLE t (id BIGINT PRIMARY KEY);", DatabaseProvider.PostgreSql).Tables.Single();
        Assert.NotNull(inline.PrimaryKey);
        Assert.Equal(new[] { "id" }, inline.PrimaryKey!.ColumnNames);

        var composite = SqlSchemaParser.ParseAs(
            "CREATE TABLE t (a INT, b INT, CONSTRAINT pk PRIMARY KEY (a, b));",
            DatabaseProvider.PostgreSql).Tables.Single();
        Assert.Equal("pk", composite.PrimaryKey!.Name);
        Assert.Equal(new[] { "a", "b" }, composite.PrimaryKey.ColumnNames);
    }

    [Fact]
    public void Parses_table_level_foreign_key_with_referential_actions()
    {
        const string sql = """
            CREATE TABLE orders (
                id BIGINT PRIMARY KEY,
                user_id BIGINT NOT NULL,
                CONSTRAINT fk_orders_user FOREIGN KEY (user_id)
                    REFERENCES users (id) ON DELETE CASCADE ON UPDATE RESTRICT
            );
            """;

        var fk = SqlSchemaParser.ParseAs(sql, DatabaseProvider.PostgreSql).Tables.Single().ForeignKeys.Single();

        Assert.Equal("fk_orders_user", fk.Name);
        Assert.Equal(new[] { "user_id" }, fk.Columns);
        Assert.Equal("users", fk.ReferencedTable);
        Assert.Equal(new[] { "id" }, fk.ReferencedColumns);
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
        Assert.Equal(ReferentialAction.Restrict, fk.OnUpdate);
    }

    [Fact]
    public void Parses_inline_column_reference_as_foreign_key()
    {
        const string sql = "CREATE TABLE t (id BIGINT PRIMARY KEY, parent BIGINT REFERENCES t (id) ON DELETE SET NULL);";

        var fk = SqlSchemaParser.ParseAs(sql, DatabaseProvider.PostgreSql).Tables.Single().ForeignKeys.Single();

        Assert.Equal(new[] { "parent" }, fk.Columns);
        Assert.Equal("t", fk.ReferencedTable);
        Assert.Equal(new[] { "id" }, fk.ReferencedColumns);
        Assert.Equal(ReferentialAction.SetNull, fk.OnDelete);
    }

    [Fact]
    public void Parses_unique_and_plain_indexes_from_table_body()
    {
        const string sql = """
            CREATE TABLE `t` (
                `id` BIGINT PRIMARY KEY,
                `sku` VARCHAR(64) NOT NULL,
                `name` VARCHAR(100),
                UNIQUE KEY `ux_t_sku` (`sku`),
                KEY `ix_t_name` (`name`)
            );
            """;

        var indexes = SqlSchemaParser.ParseAs(sql, DatabaseProvider.MySql).Tables.Single().Indexes;

        var unique = Assert.Single(indexes, i => i.Name == "ux_t_sku");
        Assert.Equal(IndexType.Unique, unique.Type);
        Assert.Equal("sku", unique.Columns.Single().ColumnName);

        var plain = Assert.Single(indexes, i => i.Name == "ix_t_name");
        Assert.Equal(IndexType.NonUnique, plain.Type);
    }

    [Fact]
    public void Parses_standalone_create_index_and_attaches_to_table()
    {
        const string sql = """
            CREATE TABLE t (id BIGINT PRIMARY KEY, email VARCHAR(320));
            CREATE UNIQUE INDEX ux_t_email ON t (email);
            CREATE INDEX ix_t_email_active ON t (email) WHERE email IS NOT NULL;
            """;

        var indexes = SqlSchemaParser.ParseAs(sql, DatabaseProvider.PostgreSql).Tables.Single().Indexes;

        Assert.Equal(2, indexes.Count);
        var unique = Assert.Single(indexes, i => i.Name == "ux_t_email");
        Assert.Equal(IndexType.Unique, unique.Type);
        var partial = Assert.Single(indexes, i => i.Name == "ix_t_email_active");
        Assert.Equal("email IS NOT NULL", partial.Filter);
    }

    [Fact]
    public void Parses_check_constraints_preserving_expression()
    {
        const string sql = """
            CREATE TABLE t (
                total DECIMAL(12,2) NOT NULL,
                CONSTRAINT ck_total CHECK (total >= 0)
            );
            """;

        var check = SqlSchemaParser.ParseAs(sql, DatabaseProvider.PostgreSql).Tables.Single().CheckConstraints.Single();

        Assert.Equal("ck_total", check.Name);
        Assert.Equal("total >= 0", check.Expression);
    }

    [Fact]
    public void Recognizes_identity_auto_increment_and_serial()
    {
        var identity = SqlSchemaParser.ParseAs(
            "CREATE TABLE t (id BIGINT IDENTITY(1,1) PRIMARY KEY);", DatabaseProvider.SqlServer)
            .Tables.Single().Columns.Single();
        Assert.Equal(IdentityStrategy.Identity, identity.Identity.Strategy);

        var auto = SqlSchemaParser.ParseAs(
            "CREATE TABLE `t` (`id` BIGINT AUTO_INCREMENT PRIMARY KEY);", DatabaseProvider.MySql)
            .Tables.Single().Columns.Single();
        Assert.Equal(IdentityStrategy.AutoIncrement, auto.Identity.Strategy);

        var serial = SqlSchemaParser.ParseAs(
            "CREATE TABLE t (id SERIAL PRIMARY KEY);", DatabaseProvider.PostgreSql)
            .Tables.Single().Columns.Single();
        Assert.Equal(IdentityStrategy.AutoIncrement, serial.Identity.Strategy);
        Assert.Equal(DbDataType.Integer, serial.Type.DataType);
    }

    [Theory]
    [InlineData("created_at TIMESTAMPTZ DEFAULT now()", DefaultValueKind.CurrentTimestamp)]
    [InlineData("created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP", DefaultValueKind.CurrentTimestamp)]
    [InlineData("status VARCHAR(20) DEFAULT 'pending'", DefaultValueKind.Literal)]
    [InlineData("stock INT DEFAULT 0", DefaultValueKind.Literal)]
    [InlineData("uid UUID DEFAULT gen_random_uuid()", DefaultValueKind.Function)]
    [InlineData("val INT DEFAULT (1 + 2)", DefaultValueKind.Expression)]
    public void Classifies_column_defaults(string columnSql, DefaultValueKind expected)
    {
        var column = SqlSchemaParser.ParseAs(
            $"CREATE TABLE t ({columnSql});", DatabaseProvider.PostgreSql).Tables.Single().Columns.Single();

        Assert.Equal(expected, column.Default.Kind);
    }

    [Fact]
    public void Preserves_literal_default_text()
    {
        var column = SqlSchemaParser.ParseAs(
            "CREATE TABLE t (status VARCHAR(20) DEFAULT 'pending');", DatabaseProvider.PostgreSql)
            .Tables.Single().Columns.Single();

        Assert.Equal("'pending'", column.Default.Value);
    }

    [Fact]
    public void Parses_stored_generated_column()
    {
        const string sql = """
            CREATE TABLE t (
                qty INT NOT NULL,
                price DECIMAL(12,2) NOT NULL,
                line_total DECIMAL(12,2) GENERATED ALWAYS AS (qty * price) STORED
            );
            """;

        var column = SqlSchemaParser.ParseAs(sql, DatabaseProvider.PostgreSql)
            .Tables.Single().Columns.Single(c => c.Name == "line_total");

        Assert.NotNull(column.Generated);
        Assert.Equal(GeneratedKind.Stored, column.Generated!.Kind);
        Assert.Equal("qty * price", column.Generated.Expression);
    }

    [Fact]
    public void Applies_alter_table_add_foreign_key_to_existing_table()
    {
        const string sql = """
            CREATE TABLE orders (id BIGINT PRIMARY KEY, user_id BIGINT NOT NULL);
            ALTER TABLE orders ADD CONSTRAINT fk_orders_user
                FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE CASCADE;
            """;

        var fk = SqlSchemaParser.ParseAs(sql, DatabaseProvider.PostgreSql).Tables.Single().ForeignKeys.Single();

        Assert.Equal("fk_orders_user", fk.Name);
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
    }

    [Fact]
    public void Skips_unmodeled_statements_but_keeps_tables()
    {
        const string sql = """
            CREATE SCHEMA app;
            CREATE TABLE t (id BIGINT PRIMARY KEY);
            CREATE VIEW v AS SELECT id FROM t;
            """;

        var schema = SqlSchemaParser.ParseAs(sql, DatabaseProvider.PostgreSql);

        Assert.Single(schema.Tables);
        Assert.Equal("t", schema.Tables[0].Name);
    }

    [Fact]
    public void Parses_qualified_table_name_into_schema_and_name()
    {
        var table = SqlSchemaParser.ParseAs(
            "CREATE TABLE public.users (id BIGINT PRIMARY KEY);", DatabaseProvider.PostgreSql).Tables.Single();

        Assert.Equal("public", table.Schema);
        Assert.Equal("users", table.Name);
    }

    [Fact]
    public void Throws_when_no_tables_are_found()
    {
        Assert.Throws<SqlParseException>(
            () => SqlSchemaParser.ParseAs("SELECT 1;", DatabaseProvider.PostgreSql));
    }

    [Fact]
    public void Import_detects_dialect()
    {
        var result = SqlSchemaParser.Import("CREATE TABLE `t` (`id` BIGINT AUTO_INCREMENT PRIMARY KEY) ENGINE=InnoDB;");

        Assert.Equal(DatabaseProvider.MySql, result.Provider);
        Assert.True(result.Detected);
    }

    [Fact]
    public void Import_with_explicit_provider_is_not_detected()
    {
        var result = SqlSchemaParser.Import(
            "CREATE TABLE t (id BIGINT PRIMARY KEY);", DatabaseProvider.Sqlite);

        Assert.Equal(DatabaseProvider.Sqlite, result.Provider);
        Assert.False(result.Detected);
    }
}
