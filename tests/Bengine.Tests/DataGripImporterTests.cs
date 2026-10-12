namespace Bengine.Tests;

using Bengine.Enums;
using Bengine.Introspection.Servers;

/// <summary>
/// Tests the DataGrip import against files in the format DataGrip's serializer
/// writes (<c>LocalDataSourceSerialization</c>): attributes <c>name</c>,
/// <c>group</c>, <c>uuid</c>; child elements <c>driver-ref</c>, <c>jdbc-url</c>;
/// user names in the sibling <c>dataSources.local.xml</c>.
/// </summary>
public sealed class DataGripImporterTests : IDisposable
{
    private readonly string _project = Directory.CreateTempSubdirectory("bengine_datagrip_").FullName;

    public DataGripImporterTests()
    {
        var idea = Directory.CreateDirectory(Path.Combine(_project, ".idea")).FullName;
        File.WriteAllText(Path.Combine(idea, "dataSources.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <project version="4">
              <component name="DataSourceManagerImpl" format="xml" multifile-model="true">
                <data-source source="LOCAL" name="prod-pg" group="Production" uuid="11111111-1111-1111-1111-111111111111">
                  <driver-ref>postgresql</driver-ref>
                  <synchronize>true</synchronize>
                  <jdbc-driver>org.postgresql.Driver</jdbc-driver>
                  <jdbc-url>jdbc:postgresql://db.example.com:6432/sales?sslmode=require</jdbc-url>
                  <working-dir>$ProjectFileDir$</working-dir>
                </data-source>
                <data-source source="LOCAL" name="shop-mysql" uuid="22222222-2222-2222-2222-222222222222">
                  <driver-ref>mysql.8</driver-ref>
                  <jdbc-url>jdbc:mysql://localhost:3307/shop</jdbc-url>
                </data-source>
                <data-source source="LOCAL" name="erp-mssql" uuid="33333333-3333-3333-3333-333333333333">
                  <driver-ref>sqlserver.ms</driver-ref>
                  <jdbc-url>jdbc:sqlserver://sql01\SQLEXPRESS:1433;databaseName=erp;encrypt=true;trustServerCertificate=true</jdbc-url>
                </data-source>
                <data-source source="LOCAL" name="local-file" uuid="44444444-4444-4444-4444-444444444444">
                  <driver-ref>sqlite.xerial</driver-ref>
                  <jdbc-url>jdbc:sqlite:$PROJECT_DIR$/data/app.db</jdbc-url>
                </data-source>
                <data-source source="LOCAL" name="legacy-oracle" uuid="55555555-5555-5555-5555-555555555555">
                  <driver-ref>oracle</driver-ref>
                  <jdbc-url>jdbc:oracle:thin:@db:1521:ORCL</jdbc-url>
                </data-source>
              </component>
            </project>
            """);
        File.WriteAllText(Path.Combine(idea, "dataSources.local.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <project version="4">
              <component name="dataSourceStorageLocal" created-in="DB-262.10968.148">
                <data-source name="prod-pg" uuid="11111111-1111-1111-1111-111111111111">
                  <secret-storage>master_key</secret-storage>
                  <user-name>reporter</user-name>
                </data-source>
                <data-source name="shop-mysql" uuid="22222222-2222-2222-2222-222222222222">
                  <user-name>root</user-name>
                </data-source>
              </component>
            </project>
            """);
    }

    public void Dispose() => Directory.Delete(_project, recursive: true);

    [Fact]
    public void Finds_the_project_file_from_a_project_directory()
    {
        var files = DataGripImporter.FindConfigFiles(_project);

        Assert.Contains(Path.Combine(_project, ".idea", "dataSources.xml"), files);
    }

    [Fact]
    public void Maps_postgres_with_group_user_and_ssl()
    {
        var pg = Import().Servers.Single(s => s.Server.Name == "prod-pg").Server;

        Assert.Equal(DatabaseProvider.PostgreSql, pg.Provider);
        Assert.Equal("Production", pg.Group);
        Assert.Equal("db.example.com", pg.Host);
        Assert.Equal(6432, pg.Port);
        Assert.Equal("sales", pg.MaintenanceDatabase);
        Assert.Equal("reporter", pg.Username);
        Assert.Equal("SSL Mode=Require", pg.Options);
        Assert.Null(pg.Password);
    }

    [Fact]
    public void Maps_mysql_sqlserver_and_sqlite()
    {
        var servers = Import().Servers.ToDictionary(s => s.Server.Name, s => s.Server);

        var mysql = servers["shop-mysql"];
        Assert.Equal((DatabaseProvider.MySql, "localhost", 3307, "shop", "root", "DataGrip"),
            (mysql.Provider, mysql.Host, mysql.Port, mysql.MaintenanceDatabase, mysql.Username, mysql.Group));

        var mssql = servers["erp-mssql"];
        Assert.Equal((DatabaseProvider.SqlServer, @"sql01\SQLEXPRESS", 1433, "erp"), (mssql.Provider, mssql.Host, mssql.Port, mssql.MaintenanceDatabase));
        Assert.Contains("TrustServerCertificate=true", mssql.Options);

        var sqlite = servers["local-file"];
        Assert.Equal(DatabaseProvider.Sqlite, sqlite.Provider);
        Assert.Equal(Path.Combine(_project, "data", "app.db"), sqlite.FilePath);
    }

    [Fact]
    public void Skips_unsupported_drivers_with_a_reason()
    {
        var skipped = Assert.Single(Import().Skipped);

        Assert.Equal("legacy-oracle", skipped.Name);
        Assert.Contains("oracle", skipped.Reason);
    }

    [Theory]
    [InlineData("jdbc:postgresql:mydb", "localhost", null, "mydb")]
    [InlineData("jdbc:postgresql://h1:5433,h2:5434/app", "h1", 5433, "app")]
    [InlineData("jdbc:postgresql://[::1]:5432/app", "::1", 5432, "app")]
    [InlineData("jdbc:postgresql://localhost/", "localhost", null, "postgres")]
    public void Parses_postgres_url_variants(string url, string host, int? port, string database)
    {
        Assert.True(DataGripImporter.TryMap("x", "g", url, null, out var profile, out _));
        Assert.Equal((host, port, database), (profile!.Host, profile.Port, profile.MaintenanceDatabase));
    }

    private DataGripImport Import() => DataGripImporter.Read(DataGripImporter.FindConfigFiles(_project).Where(f => f.StartsWith(_project, StringComparison.Ordinal)));
}
