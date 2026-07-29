namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Parsing;

public class SqlTypeResolverTests
{
    [Theory]
    [InlineData("int", DbDataType.Integer)]
    [InlineData("INTEGER", DbDataType.Integer)]
    [InlineData("bigint", DbDataType.BigInt)]
    [InlineData("varchar", DbDataType.VarChar)]
    [InlineData("nvarchar", DbDataType.VarChar)]
    [InlineData("character varying", DbDataType.VarChar)]
    [InlineData("text", DbDataType.Text)]
    [InlineData("longtext", DbDataType.Text)]
    [InlineData("numeric", DbDataType.Decimal)]
    [InlineData("double precision", DbDataType.Double)]
    [InlineData("bytea", DbDataType.Blob)]
    [InlineData("jsonb", DbDataType.JsonBinary)]
    [InlineData("uniqueidentifier", DbDataType.Uuid)]
    [InlineData("timestamptz", DbDataType.TimestampTz)]
    [InlineData("inet", DbDataType.Text)]
    [InlineData("cidr", DbDataType.Text)]
    [InlineData("interval", DbDataType.Text)]
    [InlineData("tsvector", DbDataType.Text)]
    [InlineData("enum", DbDataType.Text)]
    [InlineData("set", DbDataType.Text)]
    [InlineData("year", DbDataType.SmallInt)]
    public void Resolves_common_aliases(string native, DbDataType expected)
    {
        var resolved = SqlTypeResolver.Resolve(DatabaseProvider.PostgreSql, native, null);

        Assert.Equal(expected, resolved.DataType);
        Assert.False(resolved.ImpliesAutoIncrement);
    }

    [Theory]
    [InlineData("serial", DbDataType.Integer)]
    [InlineData("bigserial", DbDataType.BigInt)]
    [InlineData("smallserial", DbDataType.SmallInt)]
    public void Serial_types_imply_auto_increment(string native, DbDataType expected)
    {
        var resolved = SqlTypeResolver.Resolve(DatabaseProvider.PostgreSql, native, null);

        Assert.Equal(expected, resolved.DataType);
        Assert.True(resolved.ImpliesAutoIncrement);
    }

    [Fact]
    public void MySql_tinyint_one_maps_to_boolean()
    {
        var resolved = SqlTypeResolver.Resolve(DatabaseProvider.MySql, "tinyint", firstArgument: 1);

        Assert.Equal(DbDataType.Boolean, resolved.DataType);
    }

    [Fact]
    public void MySql_tinyint_without_one_stays_tinyint()
    {
        var resolved = SqlTypeResolver.Resolve(DatabaseProvider.MySql, "tinyint", firstArgument: null);

        Assert.Equal(DbDataType.TinyInt, resolved.DataType);
    }

    [Fact]
    public void Unknown_type_throws()
    {
        Assert.Throws<SqlParseException>(
            () => SqlTypeResolver.Resolve(DatabaseProvider.PostgreSql, "nonsense", null));
    }
}
