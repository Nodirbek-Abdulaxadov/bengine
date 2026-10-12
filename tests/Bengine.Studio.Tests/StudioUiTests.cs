namespace Bengine.Studio.Tests;

using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bengine.Enums;
using Bengine.Introspection.Servers;
using Bengine.Studio.ViewModels;
using Bengine.Studio.Views;
using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>Builds the Studio app on Avalonia's headless platform with real Skia rendering.</summary>
public static class TestApp
{
    /// <summary>Configures the app for headless tests.</summary>
    /// <returns>The app builder.</returns>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// End-to-end UI tests of the Studio window: open it, expand the Object Explorer
/// down to a table, and open the Properties, SQL, Data, and Query Tool tabs,
/// asserting each one loads. When <c>BENGINE_SHOTS</c> names a directory, each
/// step is also rendered to a PNG there.
/// </summary>
public sealed class StudioUiTests
{
    [AvaloniaFact]
    public async Task Explores_a_sqlite_server_end_to_end()
    {
        var dir = Directory.CreateTempSubdirectory("bengine_studio_").FullName;
        var file = Path.Combine(dir, "shop.db");
        using (var connection = new SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE, age INTEGER);
                CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id), total REAL);
                INSERT INTO users (email, age) VALUES ('a@x.io', 31), ('b@x.io', 27), ('c@x.io', 45);
                """;
            command.ExecuteNonQuery();
        }

        var store = new ServerStore(Path.Combine(dir, "servers.json"));
        store.Upsert(new ServerProfile { Name = "shop (sqlite)", Provider = DatabaseProvider.Sqlite, FilePath = file });

        await WalkAsync(store, "sqlite", ["Databases", "main", "Tables"], "users", "main", "SELECT * FROM users;");
        SqliteConnection.ClearAllPools();
    }

    [AvaloniaFact]
    public async Task Explores_a_live_postgres_server_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_PG") is not { Length: > 0 } spec)
        {
            return;
        }

        var parts = spec.Split(';');
        var dir = Directory.CreateTempSubdirectory("bengine_studio_pg_").FullName;
        var store = new ServerStore(Path.Combine(dir, "servers.json"));
        store.Upsert(new ServerProfile
        {
            Name = "pg-live",
            Provider = DatabaseProvider.PostgreSql,
            Host = parts[0],
            Port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            MaintenanceDatabase = parts[2],
            Username = parts[3],
            Password = parts[4],
            SavePassword = true,
        });
        store.Upsert(new ServerProfile { Name = "remote-pg", Provider = DatabaseProvider.PostgreSql, Host = "203.0.113.1", Port = 5432, Username = "admin" });

        await WalkAsync(store, "pg", ["Databases", parts[2], "Schemas", "public", "Tables"], "Users", parts[2],
            "SELECT table_schema, count(*) AS tables\nFROM information_schema.tables\nGROUP BY 1\nORDER BY 2 DESC;");
    }

    private static async Task WalkAsync(ServerStore store, string prefix, string[] path, string table, string database, string sql)
    {
        var window = new MainWindow(store) { Width = 1440, Height = 900 };
        var vm = new ExplorerViewModel(store) { Dialogs = window };
        window.DataContext = vm;
        window.Show();
        await Until(() => vm.Roots.Count > 0);
        Shot(window, $"{prefix}-0-start");

        var item = vm.Roots[0].Children[0];
        item.IsExpanded = true;
        await Until(() => !item.IsLoading && item.Children.Count > 0 && !item.Children[0].IsPlaceholder);
        foreach (var label in path)
        {
            item = item.Children.First(c => c.Label == label);
            item.IsExpanded = true;
            await Until(() => !item.IsLoading && item.Children.Count > 0 && !item.Children[0].IsPlaceholder);
        }

        var tableItem = item.Children.First(c => c.Label == table);
        tableItem.IsExpanded = true;
        await Until(() => !tableItem.IsLoading && !tableItem.Children[0].IsPlaceholder);
        var columns = tableItem.Children.First(c => c.Label == "Columns");
        columns.IsExpanded = true;
        await Until(() => !columns.IsLoading && !columns.Children[0].IsPlaceholder);

        vm.SelectedItem = tableItem;
        await Until(() => vm.PropertiesTab.Rows.Count > 0);
        Assert.Contains(vm.PropertiesTab.Rows, r => r.Name == "Name" && r.Value == table);
        Shot(window, $"{prefix}-1-properties");

        vm.ActiveTab = vm.SqlTab;
        await Until(() => vm.SqlTab.Sql is not null);
        Assert.Contains("CREATE TABLE", vm.SqlTab.Sql!);
        Shot(window, $"{prefix}-2-sql");

        vm.OpenData(tableItem);
        var data = vm.Tabs.OfType<DataDocumentViewModel>().Single();
        await Until(() => data.Rows.Count > 0 && !data.IsBusy);
        Assert.False(data.IsError, data.Status);
        Shot(window, $"{prefix}-3-data");

        await vm.OpenQueryAsync(tableItem.Server!, database, sql);
        var query = vm.Tabs.OfType<QueryDocumentViewModel>().Single();
        await query.ExecuteAsync(null);
        await Until(() => query.Rows.Count > 0 && !query.IsBusy);
        Assert.False(query.IsError, query.Status);
        Shot(window, $"{prefix}-4-query");

        window.Close();
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 20000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("The UI did not reach the expected state in time.");
            }

            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(Avalonia.Controls.Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("BENGINE_SHOTS") is not { Length: > 0 } directory)
        {
            return;
        }

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.CaptureRenderedFrame()?.Save(Path.Combine(directory, name + ".png"));
    }
}
