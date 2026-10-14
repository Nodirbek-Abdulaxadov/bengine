namespace Bengine.Tests;

using System.Globalization;
using System.Text;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Design;
using Bengine.Introspection.Export;
using Bengine.Introspection.Import;
using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Tree;
using Xunit.Abstractions;

/// <summary>
/// Tests delimited-file import: type inference, quoting, the new-table design,
/// batching under each provider's parameter limit, and all-or-nothing failure.
/// SQLite always; the live providers in scratch databases when configured.
/// </summary>
[Collection("live-postgres")]
public sealed class DataImportTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bengine_import_").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Exports_and_parses_xlsx_and_csv_round_trips()
    {
        var rows = new List<IReadOnlyList<object?>> { new object?[] { 1L, "a, \"quoted\"\nline", null, true } };
        var csv = DataExporter.ToCsv(["id", "text", "empty", "flag"], rows);
        var parsed = CsvText.Parse(csv, ',');
        Assert.Equal(["1", "a, \"quoted\"\nline", string.Empty, "True"], parsed[1].ToArray());

        var bytes = XlsxWriter.Write("Sheet", ["id", "text"], rows);
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
        Assert.Contains(zip.Entries, e => e.FullName == "xl/worksheets/sheet1.xml");
    }

    [Fact]
    public async Task Imports_a_csv_into_a_new_sqlite_table()
    {
        var file = Write("people.csv", "Full Name;Age;Score;Active;Joined\n\"Doe; John\";42;3.5;yes;2024-01-02\nJane;;7;no;2023-12-31\n");
        var preview = DataImporter.Preview(file, new ImportOptions(), DatabaseProvider.Sqlite);
        Assert.Equal(';', preview.Delimiter);
        Assert.Equal(["Full Name", "Age", "Score", "Active", "Joined"], preview.Headers.ToArray());
        Assert.Equal(["TEXT", "INTEGER", "REAL", "INTEGER", "TEXT"], preview.Types.ToArray());

        var path = Path.Combine(_dir, "db.sqlite");
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, $"Data Source={path}");
        var design = DataImporter.DesignTable(preview, DatabaseProvider.Sqlite, null, "people");
        foreach (var statement in design.Script())
        {
            await session.Query.ExecuteAllAsync(statement);
        }

        var mapping = design.Columns.Select((c, i) => new ColumnMapping(i, c.Name)).ToList();
        var count = await DataImporter.ImportAsync(session, new TableRef("people"), file, new ImportOptions { Delimiter = preview.Delimiter }, mapping,
            new Dictionary<string, string> { ["active"] = "INTEGER" });

        Assert.Equal(2, count);
        var rows = (await session.Query.ExecuteAsync("SELECT full_name, age, active FROM people ORDER BY full_name")).Rows;
        Assert.Equal("Doe; John", rows[0][0]);
        Assert.Null(rows[1][1]);
        Assert.Equal(1L, rows[0][2]);
    }

    [Fact]
    public async Task A_failing_row_imports_nothing()
    {
        var path = Path.Combine(_dir, "db.sqlite");
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, $"Data Source={path}");
        await session.Query.ExecuteAllAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT NOT NULL)");
        var file = Write("bad.csv", "id,name\n1,a\n2,\n");

        await Assert.ThrowsAsync<IntrospectionException>(() =>
            DataImporter.ImportAsync(session, new TableRef("t"), file, new ImportOptions(), [new(0, "id"), new(1, "name")]));
        Assert.Equal(0L, (await session.Query.ExecuteAsync("SELECT count(*) FROM t")).Rows[0][0]);
    }

    [Fact]
    public async Task Imports_thousands_of_rows_on_live_servers_when_configured()
    {
        var builder = new StringBuilder("id,name,amount,born\n");
        for (var i = 1; i <= 5000; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{i},\"name, {i}\",{i * 1.25m},2000-01-{(i % 28) + 1:00}\n");
        }

        var file = Write("big.csv", builder.ToString());
        foreach (var (variable, provider) in new[] { ("BENGINE_TEST_PG", DatabaseProvider.PostgreSql), ("BENGINE_TEST_MYSQL", DatabaseProvider.MySql), ("BENGINE_TEST_MSSQL", DatabaseProvider.SqlServer) })
        {
            if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 } spec)
            {
                continue;
            }

            var p = spec.Split(';');
            var admin = new ServerProfile
            {
                Name = variable, Provider = provider, Host = p[0], Port = int.Parse(p[1], CultureInfo.InvariantCulture), MaintenanceDatabase = p[2],
                Username = p[3], Password = p[4], SavePassword = true, Options = provider == DatabaseProvider.SqlServer ? "TrustServerCertificate=True" : null,
            };
            var scratch = $"bengine_imp_{Guid.NewGuid():N}"[..22];
            await using (var a = await ObjectExplorer.OpenSessionAsync(admin, null, null))
            {
                await a.Query.ExecuteAllAsync($"CREATE DATABASE {scratch}");
            }

            try
            {
                await using var session = await ObjectExplorer.OpenSessionAsync(admin, null, scratch);
                var schema = provider switch { DatabaseProvider.PostgreSql => "public", DatabaseProvider.SqlServer => "dbo", _ => scratch };
                var preview = DataImporter.Preview(file, new ImportOptions(), provider);
                var design = DataImporter.DesignTable(preview, provider, schema, "big");
                foreach (var statement in design.Script())
                {
                    await session.Query.ExecuteAllAsync(statement);
                }

                var count = await DataImporter.ImportAsync(session, new TableRef("big", schema), file, new ImportOptions(),
                    design.Columns.Select((c, i) => new ColumnMapping(i, c.Name)).ToList());
                output.WriteLine($"{variable}: {string.Join(", ", design.Columns.Select(c => c.Name + " " + c.Type))} → {count} rows");
                Assert.Equal(5000, count);
                var sum = (await session.Query.ExecuteAsync("SELECT SUM(amount) FROM big")).Rows[0][0];
                Assert.Equal(15628125m, Convert.ToDecimal(sum, CultureInfo.InvariantCulture));
            }
            finally
            {
                Npgsql.NpgsqlConnection.ClearAllPools();
                MySqlConnector.MySqlConnection.ClearAllPools();
                Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
                await using var a = await ObjectExplorer.OpenSessionAsync(admin, null, null);
                if (provider == DatabaseProvider.SqlServer)
                {
                    await a.Query.ExecuteAllAsync($"ALTER DATABASE {scratch} SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
                }

                await a.Query.ExecuteAllAsync(provider == DatabaseProvider.PostgreSql ? $"DROP DATABASE {scratch} WITH (FORCE)" : $"DROP DATABASE {scratch}");
            }
        }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }
}
