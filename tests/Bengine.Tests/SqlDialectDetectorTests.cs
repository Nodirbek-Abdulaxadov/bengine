namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Parsing;

public class SqlDialectDetectorTests
{
    [Fact]
    public void Detects_postgres_from_serial_and_jsonb()
    {
        const string sql = "CREATE TABLE t (id BIGSERIAL PRIMARY KEY, data JSONB, ts TIMESTAMPTZ);";

        Assert.Equal(DatabaseProvider.PostgreSql, SqlDialectDetector.Detect(sql));
    }

    [Fact]
    public void Detects_mysql_from_backticks_and_auto_increment()
    {
        const string sql = "CREATE TABLE `t` (`id` BIGINT NOT NULL AUTO_INCREMENT) ENGINE=InnoDB;";

        Assert.Equal(DatabaseProvider.MySql, SqlDialectDetector.Detect(sql));
    }

    [Fact]
    public void Detects_sqlserver_from_brackets_and_identity()
    {
        const string sql = "CREATE TABLE [dbo].[t] ([id] BIGINT IDENTITY(1,1), [n] NVARCHAR(50));";

        Assert.Equal(DatabaseProvider.SqlServer, SqlDialectDetector.Detect(sql));
    }

    [Fact]
    public void Detects_sqlite_from_autoincrement()
    {
        const string sql = "CREATE TABLE t (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT);";

        Assert.Equal(DatabaseProvider.Sqlite, SqlDialectDetector.Detect(sql));
    }

    [Fact]
    public void Defaults_to_postgres_when_no_signal_is_present()
    {
        const string sql = "CREATE TABLE t (id INT, name VARCHAR(10));";

        Assert.Equal(DatabaseProvider.PostgreSql, SqlDialectDetector.Detect(sql));
    }
}
