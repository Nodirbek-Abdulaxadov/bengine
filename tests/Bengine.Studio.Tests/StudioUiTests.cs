namespace Bengine.Studio.Tests;

using System.Diagnostics;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.LogicalTree;
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

        // A scratch database, so the configured one is never written to.
        var parts = spec.Split(';');
        var scratch = $"bengine_ui_{Guid.NewGuid():N}"[..24];
        var admin = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = parts[0], Port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), Database = parts[2], Username = parts[3], Password = parts[4],
        }.ConnectionString;
        await Execute(admin, $"CREATE DATABASE {scratch}");
        try
        {
            await Execute(new Npgsql.NpgsqlConnectionStringBuilder(admin) { Database = scratch }.ConnectionString, """
                CREATE TABLE "Users" (id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, email text NOT NULL, roles text[], seen timestamptz DEFAULT now());
                INSERT INTO "Users" (email, roles) VALUES ('a@x.io', '{admin}'), ('b@x.io', '{}');
                """);

            var dir = Directory.CreateTempSubdirectory("bengine_studio_pg_").FullName;
            var store = new ServerStore(Path.Combine(dir, "servers.json"));
            store.Upsert(new ServerProfile
            {
                Name = "pg-live",
                Provider = DatabaseProvider.PostgreSql,
                Host = parts[0],
                Port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                MaintenanceDatabase = scratch,
                Username = parts[3],
                Password = parts[4],
                SavePassword = true,
            });
            store.Upsert(new ServerProfile { Name = "remote-pg", Provider = DatabaseProvider.PostgreSql, Host = "203.0.113.1", Port = 5432, Username = "admin" });

            await WalkAsync(store, "pg", ["Databases", scratch, "Schemas", "public", "Tables"], "Users", scratch,
                "SELECT table_schema, count(*) AS tables\nFROM information_schema.tables\nGROUP BY 1\nORDER BY 2 DESC;");
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await Execute(admin, $"DROP DATABASE IF EXISTS {scratch} WITH (FORCE)");
        }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [AvaloniaFact]
    public async Task Shows_live_postgres_activity_and_server_dialog_when_configured()
    {
        if (Environment.GetEnvironmentVariable("BENGINE_TEST_PG") is not { Length: > 0 } spec)
        {
            return;
        }

        var parts = spec.Split(';');
        var dir = Directory.CreateTempSubdirectory("bengine_studio_act_").FullName;
        var store = new ServerStore(Path.Combine(dir, "servers.json"));
        var server = store.Upsert(new ServerProfile
        {
            Name = "pg-live", Provider = DatabaseProvider.PostgreSql, Host = parts[0], Port = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
            MaintenanceDatabase = parts[2], Username = parts[3], Password = parts[4], SavePassword = true, Color = "#e05d5d",
            SshHost = "bastion.example.com", SshUser = "deploy", SshKeyFile = "~/.ssh/id_ed25519", SslMode = "require",
        });
        var direct = server with { SshHost = null, SslMode = null };

        var table = $"bengine_act_{Guid.NewGuid():N}"[..22];
        await using var setup = await Bengine.Introspection.Tree.ObjectExplorer.OpenSessionAsync(direct, null, null);
        await setup.Query.ExecuteAllAsync($"CREATE TABLE {table} (id int PRIMARY KEY, v int); INSERT INTO {table} VALUES (1, 1)");
        await using var holder = await Bengine.Introspection.Tree.ObjectExplorer.OpenSessionAsync(direct, null, null);
        await using var waiter = await Bengine.Introspection.Tree.ObjectExplorer.OpenSessionAsync(direct, null, null);
        await holder.BeginTransactionAsync();
        await holder.Query.ExecuteAllAsync($"UPDATE {table} SET v = 2 WHERE id = 1");
        var blocked = waiter.Query.ExecuteAllAsync($"UPDATE {table} SET v = 3 WHERE id = 1");
        try
        {
            store.Upsert(direct);
            var window = new MainWindow(store) { Width = 1440, Height = 900 };
            var vm = new ExplorerViewModel(store) { Dialogs = window };
            window.DataContext = vm;
            window.Show();
            var activity = vm.OpenActivity(store.List()[0]);
            await Until(() => activity.Sessions.Any(s => s.BlockedBy is not null), 15000);
            activity.Selected = activity.Sessions.First(s => s.BlockedBy is not null);
            Shot(window, "pg-6-activity");

            var form = new ServerFormViewModel(store, server) { TabIndex = 3 };
            var dialog = new ServerDialog { DataContext = form };
            dialog.Show();
            Shot(dialog, "pg-7-server-ssh");
            form.TabIndex = 2;
            Shot(dialog, "pg-8-server-ssl");
            dialog.Close();

            await activity.CancelQueryCommand.ExecuteAsync(null);
            await Assert.ThrowsAnyAsync<Exception>(() => blocked);
            window.Close();
        }
        finally
        {
            await holder.RollbackAsync();
            await setup.Query.ExecuteAllAsync($"DROP TABLE {table}");
        }
    }

    [AvaloniaFact]
    public void Imports_datagrip_data_sources_from_a_project_folder()
    {
        var dir = Directory.CreateTempSubdirectory("bengine_studio_dg_").FullName;
        var idea = Directory.CreateDirectory(Path.Combine(dir, "project", ".idea")).FullName;
        File.WriteAllText(Path.Combine(idea, "dataSources.xml"), """
            <project version="4">
              <component name="DataSourceManagerImpl">
                <data-source source="LOCAL" name="demo-pg" group="Demo" uuid="u1">
                  <jdbc-url>jdbc:postgresql://localhost:5433/postgres</jdbc-url>
                </data-source>
                <data-source source="LOCAL" name="demo-oracle" uuid="u2">
                  <jdbc-url>jdbc:oracle:thin:@h:1521:X</jdbc-url>
                </data-source>
              </component>
            </project>
            """);
        var store = new ServerStore(Path.Combine(dir, "servers.json"));
        var dialog = new ImportDialog(store);
        dialog.Show();

        dialog.FindControl<Avalonia.Controls.Primitives.TabStrip>("Source")!.SelectedIndex = 1;
        dialog.FindControl<Avalonia.Controls.TextBox>("PathBox")!.Text = Path.Combine(dir, "project");
        var scan = dialog.GetLogicalDescendants().OfType<Avalonia.Controls.Button>().Single(b => Equals(b.Content, "Scan"));
        scan.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var import = dialog.FindControl<Avalonia.Controls.Button>("ImportButton")!;
        Assert.True(import.IsEnabled);
        Assert.Contains("oracle", dialog.FindControl<Avalonia.Controls.TextBlock>("Skipped")!.Text);
        Shot(dialog, "import-datagrip");

        import.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        var server = Assert.Single(store.List());
        Assert.Equal(("demo-pg", "Demo", 5433), (server.Name, server.Group, server.Port));
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
        await query.ExecuteAsync(query.QueryText);
        await Until(() => query.Results.Count > 0 && !query.IsBusy);
        Assert.False(query.IsError, query.Status);
        Shot(window, $"{prefix}-4-query");

        await query.ExplainAsync(sql, 0, analyze: true);
        await Until(() => !query.IsBusy);
        Assert.False(query.IsError, query.Status);
        Assert.NotNull(query.Plan);
        Shot(window, $"{prefix}-5-plan");

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
