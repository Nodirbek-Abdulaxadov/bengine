namespace Bengine.Studio.Tests;

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bengine.Enums;
using Bengine.Introspection.Servers;
using Bengine.Studio.ViewModels;
using Bengine.Studio.Views;
using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>Closing the Studio and opening it again brings the workspace back.</summary>
public sealed class WorkspaceRestoreTests
{
    [AvaloniaFact]
    public async Task Restores_tabs_tree_and_layout_after_a_restart()
    {
        var dir = Directory.CreateTempSubdirectory("bengine_workspace_").FullName;
        var database = Path.Combine(dir, "shop.db");
        using (var connection = new SqliteConnection($"Data Source={database}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL, age INTEGER);
                INSERT INTO users (email, age) VALUES ('a@x.io', 31), ('b@x.io', 27), ('c@x.io', 45);
                """;
            command.ExecuteNonQuery();
        }

        var store = new ServerStore(Path.Combine(dir, "servers.json"));
        store.Upsert(new ServerProfile { Name = "shop", Provider = DatabaseProvider.Sqlite, FilePath = database });
        var workspace = Path.Combine(dir, "workspace.json");
        var script = Path.Combine(dir, "report.sql");
        await File.WriteAllTextAsync(script, "SELECT count(*) FROM users;");

        // First run: expand the tree, open a filtered data editor, a console, an edited file, and DDL.
        var (window, vm) = Open(store, workspace);
        var server = vm.ServerItems.Single();
        server.IsExpanded = true;
        var main = await ChildAsync(server, "main");
        var tables = await ChildAsync(main, "tables");
        var users = await ChildAsync(tables, "users");
        users.IsExpanded = true;
        await Until(() => users.IsLoaded);
        vm.SelectedItem = users;

        var data = vm.OpenData(server.Server!, "main", new Bengine.Introspection.Model.TableRef("users"), "age > 30");
        data.OrderByText = "age DESC";
        var console = await vm.OpenQueryAsync(server.Server!, "main", "SELECT email\nFROM users;");
        var file = await vm.OpenFileAsync(script);
        file!.QueryText = "SELECT count(*) FROM users WHERE age > 40;";
        await vm.OpenDdlAsync(users);
        vm.ActiveTab = data;
        await Until(() => !data.IsBusy && !console!.IsBusy);
        window.FindControl<Grid>("Body")!.ColumnDefinitions[0].Width = new GridLength(420);
        window.Width = 1300;
        window.Height = 820;
        Dispatcher.UIThread.RunJobs();
        window.Close();

        var saved = WorkspaceState.Load(workspace);
        Assert.Equal(["data", "ddl", "query", "query"], saved.Tabs.Select(t => t.Kind).Order(StringComparer.Ordinal));
        Assert.Equal("data", saved.Tabs[saved.ActiveTab].Kind);
        Assert.Contains(saved.Expanded, k => k.Contains("|Table|", StringComparison.Ordinal) && k.EndsWith("|users|users|", StringComparison.Ordinal));
        Assert.EndsWith("|users|users|", saved.Selected);
        Assert.True(saved.Tabs.Single(t => t.FilePath == script).Unsaved);
        Assert.Null(saved.Tabs.Single(t => t.Kind == "query" && t.FilePath is null).FilePath);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(workspace));
        }

        // Second run: everything comes back; only the active tab connects at once.
        (window, vm) = Open(store, workspace);
        await Until(() => vm.Tabs.Count == 4);
        var restoredData = Assert.IsType<DataDocumentViewModel>(vm.ActiveTab);
        Assert.Equal(("users", "age > 30", "age DESC"), (restoredData.Table.Name, restoredData.WhereText, restoredData.OrderByText));
        await Until(() => !restoredData.IsBusy && restoredData.Rows.Count == 2);
        Assert.Equal("c@x.io", restoredData.Rows[0][1]);

        var restoredConsole = vm.Tabs.OfType<QueryDocumentViewModel>().Single(q => q.FilePath is null);
        Assert.Equal("SELECT email\nFROM users;", restoredConsole.QueryText);
        Assert.False(restoredConsole.IsConnected);
        var restoredFile = vm.Tabs.OfType<QueryDocumentViewModel>().Single(q => q.FilePath == script);
        Assert.Equal("SELECT count(*) FROM users WHERE age > 40;", restoredFile.QueryText);
        Assert.True(restoredFile.IsDirty);
        Assert.Contains("CREATE TABLE", vm.Tabs.OfType<SqlDocumentViewModel>().Single().Sql);

        // Showing the console starts it; running it works.
        vm.ActiveTab = restoredConsole;
        await restoredConsole.ExecuteAsync(restoredConsole.QueryText);
        await Until(() => restoredConsole.Results.Count > 0 && !restoredConsole.IsBusy);
        Assert.Equal(3, restoredConsole.Results[0].Rows.Count);

        // The tree reconnects SQLite (no password) and re-expands down to the table, which is selected again.
        var restoredServer = vm.ServerItems.Single();
        await Until(() => restoredServer.IsLoaded);
        var restoredUsers = await FindAsync(restoredServer, "main", "tables", "users");
        await Until(() => restoredUsers.IsExpanded && restoredUsers.IsLoaded);
        Assert.Same(restoredUsers, vm.SelectedItem);

        Assert.Equal(420, window.FindControl<Grid>("Body")!.ColumnDefinitions[0].Width.Value);
        Assert.Equal((1300, 820), (window.Width, window.Height));

        // Closing a restored tab that was never shown is fine, and the next start remembers that.
        await vm.CloseTabAsync(vm.Tabs.OfType<SqlDocumentViewModel>().Single());
        window.Close();
        Assert.Equal(3, WorkspaceState.Load(workspace).Tabs.Count);
        SqliteConnection.ClearAllPools();
    }

    [AvaloniaFact]
    public void Ignores_a_missing_or_broken_workspace_file()
    {
        var dir = Directory.CreateTempSubdirectory("bengine_workspace_").FullName;
        var path = Path.Combine(dir, "workspace.json");
        Assert.Empty(WorkspaceState.Load(path).Tabs);
        File.WriteAllText(path, "{ not json");
        Assert.Empty(WorkspaceState.Load(path).Tabs);

        // A tab of a server that was removed is dropped.
        var store = new ServerStore(Path.Combine(dir, "servers.json"));
        var state = new WorkspaceState { Tabs = [new TabState { Kind = "query", ServerId = "gone", Text = "SELECT 1" }], ActiveTab = 0 };
        var vm = new ExplorerViewModel(store);
        vm.RestoreWorkspace(state);
        Assert.Empty(vm.Tabs);
    }

    private static (MainWindow Window, ExplorerViewModel Vm) Open(ServerStore store, string workspace)
    {
        var window = new MainWindow(store) { Width = 1440, Height = 900 };
        var vm = new ExplorerViewModel(store) { Dialogs = window };
        window.DataContext = vm;
        window.RestoreWorkspace(workspace);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static async Task<TreeItemViewModel> ChildAsync(TreeItemViewModel parent, string label)
    {
        parent.IsExpanded = true;
        await Until(() => parent.IsLoaded && parent.Children.Any(c => c.Label == label));
        return parent.Children.First(c => c.Label == label);
    }

    private static async Task<TreeItemViewModel> FindAsync(TreeItemViewModel root, params string[] path)
    {
        var item = root;
        foreach (var label in path)
        {
            var parent = item;
            await Until(() => parent.IsLoaded && parent.Children.Any(c => c.Label == label));
            item = parent.Children.First(c => c.Label == label);
        }

        return item;
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
}

