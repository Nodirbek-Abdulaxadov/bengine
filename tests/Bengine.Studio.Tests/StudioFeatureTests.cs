namespace Bengine.Studio.Tests;

using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.History;
using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;
using Bengine.Studio.Controls;
using Bengine.Studio.ViewModels;
using Bengine.Studio.Views;
using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>
/// Dialogs for tests: confirmations and reviews answer yes, parameters come
/// from a preset map, and everything else is recorded or declined.
/// </summary>
internal sealed class FakeDialogs : IDialogService
{
    public Dictionary<string, string> Parameters { get; } = new();

    public List<string> Reviewed { get; } = [];

    public bool Confirm { get; set; } = true;

    public string? Clipboard { get; private set; }

    public Task<PasswordAnswer?> AskPasswordAsync(ServerProfile server, string? error) => Task.FromResult<PasswordAnswer?>(null);

    public Task<ServerProfile?> EditServerAsync(ServerProfile? server) => Task.FromResult<ServerProfile?>(null);

    public Task<int> ImportFromPgAdminAsync() => Task.FromResult(0);

    public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(Confirm);

    public string? SavePath { get; set; }

    public string? OpenPath { get; set; }

    public Task<string?> SaveFileAsync(string suggestedName) => Task.FromResult(SavePath);

    public Task<string?> OpenFileAsync(string title, IReadOnlyList<string> extensions) => Task.FromResult(OpenPath);

    public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

    public Func<IReadOnlyList<Bengine.Introspection.Projects.HistoryVersion>, string, string?>? LocalHistory { get; set; }

    public Task<string?> ShowLocalHistoryAsync(string title, IReadOnlyList<Bengine.Introspection.Projects.HistoryVersion> versions, string current) =>
        Task.FromResult(LocalHistory?.Invoke(versions, current));

    public List<Bengine.Introspection.Sql.Usage> Usages { get; } = [];

    public Task<Bengine.Introspection.Sql.Usage?> ShowUsagesAsync(string title, IReadOnlyList<Bengine.Introspection.Sql.Usage> usages)
    {
        Usages.Clear();
        Usages.AddRange(usages);
        return Task.FromResult<Bengine.Introspection.Sql.Usage?>(null);
    }

    public Func<BackupViewModel, Task>? Backup { get; set; }

    public Task ShowBackupAsync(BackupViewModel backup) => Backup?.Invoke(backup) ?? Task.CompletedTask;

    public Task<IReadOnlyDictionary<string, string>?> AskParametersAsync(IReadOnlyList<string> names, IReadOnlyDictionary<string, string> previous) =>
        Task.FromResult<IReadOnlyDictionary<string, string>?>(names.ToDictionary(n => n, n => Parameters[n]));

    public Task<bool> ReviewSqlAsync(string title, string sql, string? confirmText)
    {
        Reviewed.Add(sql);
        return Task.FromResult(confirmText is not null && Confirm);
    }

    public Task<string?> EditValueAsync(string title, string? value, bool readOnly) => Task.FromResult<string?>(null);

    public Task<string?> PromptAsync(string title, string label, string initial) => Task.FromResult<string?>(initial + "_renamed");

    public Task<QueryHistoryEntry?> ShowHistoryAsync(QueryHistory history, string? serverId) => Task.FromResult<QueryHistoryEntry?>(null);

    public Func<TableEditorViewModel, Task<bool>>? TableEditor { get; set; }

    public Task<bool> EditTableAsync(TableEditorViewModel editor) => TableEditor?.Invoke(editor) ?? Task.FromResult(false);

    public Func<ImportDataViewModel, Task<bool>>? Importer { get; set; }

    public Task<bool> ImportDataAsync(ImportDataViewModel import) => Importer?.Invoke(import) ?? Task.FromResult(false);

    public Task SetClipboardAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }

    public Task<string?> GetClipboardAsync() => Task.FromResult(Clipboard);
}

/// <summary>
/// End-to-end tests of the DataGrip-style tools on a real SQLite database:
/// completion in the editor, staged data edits, foreign-key navigation, manual
/// transactions, parameters, read-only servers, Go to object, tree filtering,
/// and object actions. With <c>BENGINE_SHOTS</c> set, steps are rendered to PNGs.
/// </summary>
public sealed class StudioFeatureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bengine_features_").FullName;
    private readonly string _file;
    private readonly ServerStore _store;
    private readonly FakeDialogs _dialogs = new();

    public StudioFeatureTests()
    {
        _file = Path.Combine(_dir, "shop.db");
        using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE, age INTEGER, note TEXT);
                CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), total REAL DEFAULT 0);
                CREATE TABLE scratch (id INTEGER PRIMARY KEY);
                INSERT INTO users (email, age, note) VALUES ('a@x.io', 31, '{"vip":true}'), ('b@x.io', 27, NULL), ('c@x.io', 45, 'hello');
                INSERT INTO orders (user_id, total) VALUES (1, 19.5), (1, 5), (3, 120);
                """;
            command.ExecuteNonQuery();
        }

        _store = new ServerStore(Path.Combine(_dir, "servers.json"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [AvaloniaFact]
    public async Task Editor_completes_tables_columns_and_foreign_key_joins()
    {
        var (window, vm) = Open(readOnly: false);
        var query = (await vm.OpenQueryAsync(Server(vm), null, string.Empty))!;
        await Until(() => query.Catalog is not null);
        var view = window.GetVisualDescendants().OfType<QueryView>().Single();
        var editor = view.GetVisualDescendants().OfType<SqlEditor>().Single();

        editor.Text = "SELECT * FROM users u JOIN ";
        editor.CaretOffset = editor.Text.Length;
        editor.ShowCompletion();
        Assert.Contains("orders o ON o.user_id = u.id", editor.CompletionLabels);
        Shot(window, "feat-1-completion-join");

        editor.Text = "SELECT u. FROM users u";
        editor.CaretOffset = "SELECT u.".Length;
        editor.ShowCompletion();
        Assert.Equal(["age", "email", "id", "note"], editor.CompletionLabels.Order().ToArray());
        Shot(window, "feat-2-completion-columns");

        editor.Text = "SELECT u.emial, o.total\nFROM users u\nJOIN ordrs o ON o.user_id = u.id\nWHERE u.note = NULL;\n\nDELETE FROM orders;";
        editor.RunInspections();
        var messages = editor.InspectionMarkers.Select(m => m.Message).ToList();
        Assert.Contains(messages, m => m.StartsWith("Unknown column emial", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Unknown table ordrs", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("Comparison with NULL", StringComparison.Ordinal));
        Assert.Contains(messages, m => m.StartsWith("DELETE without WHERE", StringComparison.Ordinal));
        Shot(window, "feat-2b-inspections");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Data_editor_stages_edits_inserts_and_deletes_until_submit()
    {
        var (window, vm) = Open(readOnly: false);
        var data = await OpenDataAsync(vm, "users");

        data.Rows[2][1] = "c2@x.io";
        data.SelectedRow = data.Rows[0];
        data.SetSelection([data.Rows[0]], 3);
        data.SetNullCommand.Execute(null);
        data.AddRowCommand.Execute(null);
        data.Rows[0][1] = "new@x.io";
        data.Rows[0][2] = "50";
        data.SetSelection([data.Rows[2]], 0);
        data.DeleteRowsCommand.Execute(null);
        Assert.Equal(4, data.PendingCount);
        Shot(window, "feat-3-pending-changes");

        await data.PreviewChangesCommand.ExecuteAsync(null);
        await Until(() => !data.IsBusy);
        Assert.False(data.IsError, data.Status);
        Assert.Equal(0, data.PendingCount);
        var preview = Assert.Single(_dialogs.Reviewed);
        Assert.Contains("DELETE FROM \"users\" WHERE \"id\" = 2;", preview);
        Assert.Contains("UPDATE \"users\" SET \"email\" = 'c2@x.io' WHERE \"id\" = 3;", preview);
        Assert.Contains("INSERT INTO \"users\" (\"email\", \"age\") VALUES ('new@x.io', 50);", preview);
        Shot(window, "feat-4-submitted");

        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, $"Data Source={_file}");
        var rows = (await session.Query.ExecuteAsync("SELECT id, email, note FROM users ORDER BY id")).Rows;
        Assert.Equal(["a@x.io", "c2@x.io", "new@x.io"], rows.Select(r => (string)r[1]!).ToArray());
        Assert.Null(rows[0][2]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Failed_submit_keeps_changes_and_rolls_back_everything()
    {
        var (window, vm) = Open(readOnly: false);
        var data = await OpenDataAsync(vm, "users");

        data.Rows[0][1] = "z@x.io";
        data.Rows[1][1] = "c@x.io"; // violates UNIQUE
        await data.SubmitCommand.ExecuteAsync(null);
        await Until(() => !data.IsBusy);

        Assert.True(data.IsError);
        Assert.Equal(2, data.PendingCount);
        await using var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, $"Data Source={_file}");
        Assert.Equal("a@x.io", (await session.Query.ExecuteAsync("SELECT email FROM users WHERE id = 1")).Rows[0][0]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Navigates_foreign_keys_both_ways()
    {
        var (window, vm) = Open(readOnly: false);
        var orders = await OpenDataAsync(vm, "orders");
        orders.SetSelection([orders.Rows[0]], 1);
        var link = Assert.Single(orders.Links);
        orders.Navigate(link, orders.Rows[0]);
        var users = vm.Tabs.OfType<DataDocumentViewModel>().Last();
        await Until(() => !users.IsBusy && users.Rows.Count > 0);
        Assert.Equal("id = 1", users.WhereText);
        Assert.Equal("a@x.io", Assert.Single(users.Rows)[1]);

        users.SetSelection([users.Rows[0]], 0);
        var back = users.Links.Single(l => !l.Outgoing);
        users.Navigate(back, users.Rows[0]);
        var related = vm.Tabs.OfType<DataDocumentViewModel>().Last();
        await Until(() => !related.IsBusy && related.Rows.Count > 0);
        Assert.Equal(2, related.Rows.Count);
        Shot(window, "feat-5-fk-navigation");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Console_runs_statements_with_parameters_and_manual_transactions()
    {
        var (window, vm) = Open(readOnly: false);
        var query = (await vm.OpenQueryAsync(Server(vm), null, string.Empty))!;
        _dialogs.Parameters["min"] = "30";

        await query.ExecuteAsync("SELECT email FROM users WHERE age > :min ORDER BY id; SELECT count(*) AS n FROM orders");
        Assert.False(query.IsError, query.Status);
        Assert.Equal(2, query.Results.Count);
        Assert.Equal(["a@x.io", "c@x.io"], query.Results[0].Rows.Select(r => r[0] ?? string.Empty).ToArray());

        query.TransactionMode = TransactionMode.Manual;
        await query.ExecuteAsync("UPDATE users SET age = 99 WHERE id = 1");
        Assert.True(query.InTransaction);
        Shot(window, "feat-6-manual-transaction");
        await query.RollbackCommand.ExecuteAsync(null);
        Assert.False(query.InTransaction);
        await query.ExecuteAsync("SELECT age FROM users WHERE id = 1");
        await query.CommitCommand.ExecuteAsync(null);
        Assert.Equal(31L, query.Results[^1].Rows[0].CurrentValue(0));

        await query.ExecuteAsync("SELECT * FROM no_such_table");
        Assert.True(query.IsError);
        Assert.Contains(query.Messages, m => m.Kind == MessageKind.Error && m.Text.Contains("no_such_table", StringComparison.Ordinal));
        Assert.NotEmpty(vm.History.List(Server(vm).Id));

        await query.ExplainAsync("SELECT * FROM orders WHERE user_id = 1", 0, analyze: false);
        Assert.NotNull(query.Plan);
        Assert.Contains(query.Plan!.Rows, r => r.Node.Operation.StartsWith("SCAN", StringComparison.Ordinal) && r.Object == "orders");
        Shot(window, "feat-7-explain");
        await query.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Read_only_servers_refuse_changes()
    {
        var (window, vm) = Open(readOnly: true);
        var query = (await vm.OpenQueryAsync(Server(vm), null, string.Empty))!;
        await query.ExecuteAsync("DELETE FROM orders WHERE id = 1");
        Assert.True(query.IsError);
        Assert.Contains("read-only", query.Status, StringComparison.Ordinal);

        var data = await OpenDataAsync(vm, "users");
        Assert.False(data.CanEdit);
        data.AddRowCommand.Execute(null);
        Assert.Equal(0, data.PendingCount);
        Shot(window, "feat-8-read-only");
        await query.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Filters_the_tree_finds_objects_and_drops_tables()
    {
        var (window, vm) = Open(readOnly: false);
        var tables = await ExpandToTablesAsync(vm);

        vm.FilterText = "ord";
        Assert.True(tables.Children.Single(c => c.Label == "orders").IsVisible);
        Assert.False(tables.Children.Single(c => c.Label == "users").IsVisible);
        Shot(window, "feat-9-tree-filter");
        vm.FilterText = string.Empty;

        await vm.GetCatalogAsync(Server(vm), "main");
        var hit = (await vm.SearchObjectsAsync("oi")).FirstOrDefault() ?? (await vm.SearchObjectsAsync("ord")).First();
        Assert.Equal("orders", hit.Relation.Name);
        Assert.Equal("orders", (await vm.RevealAsync(hit))!.Label);

        var scratch = tables.Children.Single(c => c.Label == "scratch");
        await vm.DropAsync(scratch);
        Assert.Contains("DROP TABLE scratch;", _dialogs.Reviewed);
        await Until(() => tables.Children.All(c => c.Label != "scratch"));

        await vm.GenerateSqlAsync(tables.Children.Single(c => c.Label == "orders"), "insert");
        var generated = vm.Tabs.OfType<QueryDocumentViewModel>().Last();
        Assert.StartsWith("INSERT INTO orders (id, user_id, total)\nVALUES (:id, :user_id, :total);", generated.QueryText, StringComparison.Ordinal);
        await generated.DisposeAsync();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Modifies_and_creates_tables_with_the_table_editor()
    {
        var (window, vm) = Open(readOnly: false);
        var tables = await ExpandToTablesAsync(vm);
        string? preview = null;
        _dialogs.TableEditor = async editor =>
        {
            var dialog = new TableEditorDialog { DataContext = editor, Width = 1100, Height = 760 };
            dialog.Show();
            var total = editor.Columns.Single(c => c.Name == "total");
            total.Name = "amount";
            total.Nullable = false;
            total.Default = "0";
            editor.SelectedColumn = editor.Columns.Single(c => c.Name == "user_id");
            editor.AddIndexCommand.Execute(null);
            editor.Indexes[^1].Name = "ix_orders_user";
            editor.Refresh();
            preview = editor.Preview;
            Shot(dialog, "feat-10-table-editor");
            var closed = false;
            editor.Closed += _ => closed = true;
            await editor.ApplyCommand.ExecuteAsync(null);
            Assert.True(closed, editor.Status);
            dialog.Close();
            return true;
        };

        await vm.ModifyTableAsync(tables.Children.Single(c => c.Label == "orders"));
        Assert.Contains("CREATE INDEX \"ix_orders_user\"", preview, StringComparison.Ordinal);
        await using (var session = await DatabaseSession.OpenAsync(DatabaseProvider.Sqlite, $"Data Source={_file}"))
        {
            var table = await session.Introspector.IntrospectTableAsync(new TableRef("orders"));
            Assert.Equal(["id", "user_id", "amount"], table.Columns.Select(c => c.Name).ToArray());
            Assert.False(table.Columns[2].IsNullable);
            Assert.Equal("ix_orders_user", Assert.Single(table.Indexes).Name);
            Assert.Equal(3, (await session.Query.ExecuteAsync("SELECT count(*) FROM orders")).Rows[0][0] is long n ? (int)n : -1);
        }

        _dialogs.TableEditor = async editor =>
        {
            editor.Name = "audit";
            editor.AddColumnCommand.Execute(null);
            editor.Columns[^1].Name = "note";
            editor.Columns[^1].Type = "TEXT";
            await editor.ApplyCommand.ExecuteAsync(null);
            return true;
        };
        await vm.NewTableAsync(tables);
        await Until(() => tables.Children.Any(c => c.Label == "audit"));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Compares_two_databases_and_applies_the_migration()
    {
        var other = Path.Combine(_dir, "shop_v2.db");
        using (var connection = new SqliteConnection($"Data Source={other}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE, age INTEGER, note TEXT, country TEXT DEFAULT 'UZ');
                CREATE TABLE orders (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), total REAL DEFAULT 0, paid INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE coupons (code TEXT PRIMARY KEY, percent INTEGER NOT NULL);
                CREATE INDEX ix_orders_paid ON orders(paid);
                """;
            command.ExecuteNonQuery();
        }

        _store.Upsert(new ServerProfile { Name = "shop v2", Provider = DatabaseProvider.Sqlite, FilePath = other });
        var (window, vm) = Open(readOnly: false);
        var servers = _store.List();
        var compare = await vm.OpenCompareAsync();
        await compare.Source.SelectAsync(servers.Single(s => s.Name == "shop v2"), "main", null);
        await compare.Target.SelectAsync(servers.Single(s => s.Name == "shop"), "main", null);
        await compare.CompareCommand.ExecuteAsync(null);
        Assert.False(compare.IsError, compare.Status);
        Assert.Contains(compare.Changes, c => c.Description == "Create table coupons");
        Assert.Contains(compare.Changes, c => c.Description == "Drop table scratch" && c.IsDestructive);
        Assert.Contains(compare.Changes, c => c.Description.StartsWith("Add column country", StringComparison.Ordinal));
        compare.Changes.Single(c => c.Description == "Drop table scratch").Include = false;
        Assert.DoesNotContain("DROP TABLE \"scratch\"", compare.Script, StringComparison.Ordinal);
        compare.SelectedChange = compare.Changes.First(c => c.Marker == "+");
        Shot(window, "feat-11-schema-compare");

        await compare.ApplyCommand.ExecuteAsync(null);
        await Until(() => !compare.IsBusy);
        Assert.False(compare.IsError, compare.Status);
        Assert.Equal(["Drop table scratch"], compare.Changes.Select(c => c.Description).ToArray());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Draws_an_er_diagram_and_exports_it()
    {
        using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE categories (id INTEGER PRIMARY KEY, name TEXT NOT NULL, parent_id INTEGER REFERENCES categories(id));
                CREATE TABLE products (id INTEGER PRIMARY KEY, category_id INTEGER REFERENCES categories(id), name TEXT NOT NULL, price NUMERIC(10,2));
                CREATE TABLE order_items (order_id INTEGER REFERENCES orders(id), product_id INTEGER REFERENCES products(id), qty INTEGER NOT NULL, PRIMARY KEY (order_id, product_id));
                CREATE TABLE reviews (id INTEGER PRIMARY KEY, user_id INTEGER REFERENCES users(id), product_id INTEGER REFERENCES products(id), stars INTEGER, body TEXT);
                """;
            command.ExecuteNonQuery();
        }

        var (window, vm) = Open(readOnly: false);
        var databases = vm.ServerItems.First();
        await databases.EnsureLoadedAsync();
        var folder = databases.Children.First(c => c.Label == "Databases");
        await folder.EnsureLoadedAsync();
        var diagram = vm.OpenDiagram(folder.Children.First(c => c.Label == "main"))!;
        await Until(() => !diagram.IsBusy && diagram.Relations.Count > 0);
        Assert.Equal(7, diagram.Relations.Count);
        Assert.Equal(7, diagram.Keys.Count);
        await Task.Delay(100);
        Shot(window, "feat-12-er-diagram");

        var view = window.GetVisualDescendants().OfType<DiagramView>().Single();
        var png = Path.Combine(_dir, "diagram.png");
        await using (var file = File.Create(png))
        {
            view.Canvas.SavePng(file);
        }

        Assert.True(new FileInfo(png).Length > 10_000);
        if (Environment.GetEnvironmentVariable("BENGINE_SHOTS") is { Length: > 0 } shots)
        {
            File.Copy(png, Path.Combine(shots, "feat-12-er-diagram-export.png"), overwrite: true);
        }

        window.Close();
    }

    [AvaloniaFact]
    public async Task Imports_a_csv_file_into_a_new_table()
    {
        var csv = Path.Combine(_dir, "Products 2026.csv");
        File.WriteAllText(csv, "SKU\tName\tPrice\tIn Stock\nA-1\tPen, blue\t1.20\tyes\nB-2\tBook\t12\tno\nC-3\tLamp\t\tyes\n");
        var (window, vm) = Open(readOnly: false);
        var tables = await ExpandToTablesAsync(vm);
        _dialogs.Importer = async import =>
        {
            var dialog = new ImportDataDialog { DataContext = import, Width = 1040, Height = 720 };
            dialog.Show();
            import.FilePath = csv;
            Assert.Equal("products_2026", import.NewTableName);
            Assert.Equal(["sku", "name", "price", "in_stock"], import.Mappings.Select(m => m.Target).ToArray());
            Assert.Equal(["TEXT", "TEXT", "REAL", "INTEGER"], import.Mappings.Select(m => m.Type).ToArray());
            Shot(dialog, "feat-13-import");
            await import.ImportCommand.ExecuteAsync(null);
            Assert.False(import.IsError, import.Status);
            dialog.Close();
            return true;
        };

        await vm.ImportDataAsync(tables);
        var data = vm.Tabs.OfType<DataDocumentViewModel>().Single();
        await Until(() => !data.IsBusy && data.Rows.Count == 3);
        Assert.Equal("Pen, blue", data.Rows[0][1]);
        Assert.Equal(RowItem.NullText, data.Rows[2][2]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Edits_sql_files_with_save_local_history_and_attachment()
    {
        var (window, vm) = Open(readOnly: false);
        var project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(project);
        var path = Path.Combine(project, "report.sql");
        await File.WriteAllTextAsync(path, "SELECT 1;");
        vm.FilesPanel.Refresh();
        Assert.Contains(vm.FilesPanel.Roots[0].Children, f => f.Name == "report.sql");

        var tab = await vm.OpenFileAsync(path);
        Assert.NotNull(tab);
        await Until(() => tab.IsConnected || tab.Catalog is not null);
        Assert.Equal("report.sql", tab.Title);
        Assert.Equal(Server(vm).Id, vm.Files.TargetOf(path)?.ServerId);

        tab.QueryText = "SELECT * FROM users;";
        Assert.True(tab.IsDirty);
        Assert.Equal("report.sql •", tab.Title);
        Shot(window, "feat-14-sql-file-dirty");
        Assert.True(await tab.SaveAsync());
        Assert.False(tab.IsDirty);
        Assert.Equal("SELECT * FROM users;", await File.ReadAllTextAsync(path));

        tab.QueryText = "SELECT * FROM orders;";
        await tab.SaveAsync();
        var versions = vm.Files.History(path);
        Assert.Equal(2, versions.Count);

        // Restore the first saved version from local history.
        _dialogs.LocalHistory = (list, current) => File.ReadAllText(list[^1].Path);
        await tab.ShowLocalHistoryAsync();
        Assert.Equal("SELECT * FROM users;", tab.QueryText);
        Assert.True(tab.IsDirty);

        // Reopening focuses the same tab; a rename moves the tab with the file.
        Assert.Same(tab, await vm.OpenFileAsync(path));
        await vm.FilesPanel.RenameAsync(vm.FilesPanel.Roots[0].Children.Single(f => f.Name == "report.sql"));
        Assert.EndsWith("report.sql_renamed", tab.FilePath, StringComparison.Ordinal);

        // A scratch file opens as a new tab.
        vm.FilesPanel.NewScratchCommand.Execute(null);
        await Until(() => vm.Tabs.OfType<QueryDocumentViewModel>().Count() == 2);
        Assert.StartsWith("scratch_", vm.Tabs.OfType<QueryDocumentViewModel>().Last().Title, StringComparison.Ordinal);
        window.FindControl<TabControl>("SidePanels")!.SelectedIndex = 1;
        vm.FilesPanel.Refresh();
        Shot(window, "feat-15-files-panel");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Finds_usages_and_renames_references_in_open_sql()
    {
        using (var connection = new SqliteConnection($"Data Source={_file}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;";
            command.ExecuteNonQuery();
        }

        var (window, vm) = Open(readOnly: false);
        var console = await vm.OpenQueryAsync(Server(vm), "main", "SELECT o.total FROM orders o;\n-- orders in a comment\nSELECT 'orders';");
        Assert.NotNull(console);
        var tables = await ExpandToTablesAsync(vm);
        var orders = tables.Children.Single(c => c.Label == "orders");

        await vm.ShowUsagesAsync(orders);
        Assert.Contains(_dialogs.Usages, u => u.Kind == "View" && u.Name == "big_orders" && u.Definition is not null);
        Assert.Single(_dialogs.Usages, u => u.Kind == "Console");

        await vm.RenameAsync(orders);
        Assert.Contains(_dialogs.Reviewed, sql => sql.Contains("-- orders is mentioned in these definitions", StringComparison.Ordinal) && sql.Contains("View big_orders", StringComparison.Ordinal));
        Assert.Equal("SELECT o.total FROM orders_renamed o;\n-- orders in a comment\nSELECT 'orders';", console.QueryText);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Dumps_and_restores_through_the_backup_dialog_model()
    {
        var (window, vm) = Open(readOnly: false);
        var databases = vm.ServerItems.First();
        await databases.EnsureLoadedAsync();
        var main = databases.Children.First(c => c.Label == "Databases");
        await main.EnsureLoadedAsync();
        var database = main.Children.First(c => c.Label == "main");
        var dump = Path.Combine(_dir, "dump.sql");
        _dialogs.Backup = async backup =>
        {
            Assert.Equal(BackupMethod.FileCopy, backup.Methods[0].Method);
            backup.Method = backup.Methods.Single(m => m.Method == BackupMethod.SqlScript);
            backup.Path = dump;
            await backup.RunAsync();
            Assert.False(backup.IsError, backup.Status);
        };
        await vm.BackupAsync(database, restore: false);
        Assert.Contains("INSERT INTO \"users\"", await File.ReadAllTextAsync(dump), StringComparison.Ordinal);

        // Restore into a second, empty registration.
        var copy = Path.Combine(_dir, "copy.db");
        await File.WriteAllBytesAsync(copy, []);
        _store.Upsert(new ServerProfile { Name = "copy", Provider = DatabaseProvider.Sqlite, FilePath = copy });
        vm.ReloadServers();
        var copyServer = vm.ServerItems.First(s => s.Server!.Name == "copy");
        await copyServer.EnsureLoadedAsync();
        var copyDatabases = copyServer.Children.First(c => c.Label == "Databases");
        await copyDatabases.EnsureLoadedAsync();
        _dialogs.Backup = async backup =>
        {
            backup.Method = backup.Methods.Single(m => m.Method == BackupMethod.SqlScript);
            backup.Path = dump;
            backup.SingleTransaction = true;
            await backup.RunAsync();
            Assert.False(backup.IsError, backup.Status);
            Assert.Equal(1, backup.Progress);
        };
        await vm.BackupAsync(copyDatabases.Children.First(c => c.Label == "main"), restore: true);
        await using var session = await vm.OpenSessionAsync(copyServer.Server!, "main");
        Assert.Equal(3L, (await session.Query.ExecuteAsync("SELECT COUNT(*) FROM orders")).Rows[0][0]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Compares_table_data_and_applies_the_sync()
    {
        var copy = Path.Combine(_dir, "copy.db");
        File.Copy(_file, copy);
        using (var connection = new SqliteConnection($"Data Source={copy}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM orders WHERE id = 3; UPDATE orders SET total = 99 WHERE id = 2; INSERT INTO orders (id, user_id, total) VALUES (7, 2, 1);";
            command.ExecuteNonQuery();
        }

        var (window, vm) = Open(readOnly: false);
        var orders = (await ExpandToTablesAsync(vm)).Children.Single(c => c.Label == "orders");
        _store.Upsert(new ServerProfile { Name = "copy", Provider = DatabaseProvider.Sqlite, FilePath = copy });
        vm.ReloadServers();
        var compare = await vm.OpenDataCompareAsync(orders);
        var copyServer = _store.List().Single(s => s.Name == "copy");
        await compare.Target.SelectAsync(copyServer, "main", null, "orders");
        await compare.CompareAsync();
        Assert.False(compare.IsError, compare.Status);
        Assert.True(compare.Rows.Count == 3, $"{compare.Status} | {compare.Source.Label} → {compare.Target.Label}");
        Assert.Contains(compare.Rows, r => r.Marker == "≠" && r.Cells.Contains("5 → 99"));
        Assert.Contains("DELETE FROM \"orders\"", compare.Script, StringComparison.Ordinal);
        Shot(window, "feat-16-data-compare");

        await compare.ApplyAsync();
        Assert.True(compare.Result!.Identical, compare.Status);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Renders_the_phase_four_dialogs()
    {
        var (window, vm) = Open(readOnly: false);
        var server = Server(vm);
        var backup = new BackupViewModel(server, "main", restore: false, () => vm.OpenSessionAsync(server, "main"), () => vm.OpenSessionAsync(server, null), null, _dialogs, vm.Settings);
        backup.Method = backup.Methods.Single(m => m.Method == BackupMethod.SqlScript);
        backup.Path = Path.Combine(_dir, "shot.sql");
        await backup.RunAsync();
        var dialogs = new List<Window>
        {
            new BackupDialog { DataContext = backup, Width = 900, Height = 640 },
        };

        var file = Path.Combine(_dir, "q.sql");
        vm.Files.Save(file, "SELECT 1;");
        vm.Files.Save(file, "SELECT id, email\nFROM users\nWHERE age > 30;");
        Assert.Equal(2, vm.Files.History(file).Count);
        dialogs.Add(new LocalHistoryDialog("Local History — q.sql", vm.Files.History(file), "SELECT id, email, age\nFROM users\nWHERE age > 40;", DatabaseProvider.Sqlite));
        dialogs.Add(new UsagesDialog("Usages of orders",
        [
            new("View", "public.big_orders", 1, "SELECT * FROM orders WHERE total > 100", "SELECT * FROM orders WHERE total > 100"),
            new("Function", "public.order_total", 3, "SELECT sum(total) FROM orders WHERE user_id = $1"),
            new("File", file, 2, "FROM orders o JOIN users u ON u.id = o.user_id"),
        ]));
        dialogs.Add(new ActionPaletteDialog());
        dialogs.Add(new KeymapDialog(new Dictionary<string, string> { ["query.run"] = "Ctrl+R", ["query.new"] = "Ctrl+R" }));
        var names = new[] { "feat-17-backup", "feat-18-local-history", "feat-19-usages", "feat-20-action-palette", "feat-21-keymap" };
        for (var i = 0; i < dialogs.Count; i++)
        {
            dialogs[i].Show(window);
            await Until(() => dialogs[i].IsVisible);
            Shot(dialogs[i], names[i]);
            dialogs[i].Close();
        }

        Assert.Contains(backup.Log, l => l.Contains("row(s)", StringComparison.Ordinal));
        window.Close();
    }

    [Fact]
    public void Keymap_applies_overrides_and_treats_cmd_as_ctrl()
    {
        var keymap = new Bengine.Studio.Controls.Keymap(new Dictionary<string, string> { ["query.run"] = "Ctrl+R", ["file.save"] = string.Empty });
        Assert.Equal("Ctrl+R", keymap.Display("query.run"));
        Assert.Empty(keymap.Gestures("file.save"));
        Assert.Equal("Ctrl+/, Ctrl+Num /", keymap.Display("editor.comment"));
        Assert.Contains("query.runScript", keymap.BoundTo(Avalonia.Input.KeyGesture.Parse("Ctrl+Shift+Enter")));
        Assert.Equal("Ctrl+Shift+Enter", Bengine.Studio.Controls.Keymap.Format(new Avalonia.Input.KeyGesture(Avalonia.Input.Key.Enter, Avalonia.Input.KeyModifiers.Meta | Avalonia.Input.KeyModifiers.Shift)));
        Assert.Empty(Bengine.Studio.Controls.Keymap.Parse("Ctrl+NoSuchKey"));
        Assert.All(Bengine.Studio.Controls.Keymap.Actions, a => Assert.True(a.Default.Length == 0 || Bengine.Studio.Controls.Keymap.Parse(a.Default).Count > 0, a.Id));
    }

    private (MainWindow Window, ExplorerViewModel Vm) Open(bool readOnly)
    {
        _store.Upsert(new ServerProfile { Name = "shop", Provider = DatabaseProvider.Sqlite, FilePath = _file, ReadOnly = readOnly, Color = readOnly ? "#e05d5d" : null });
        var window = new MainWindow(_store) { Width = 1440, Height = 900 };
        var settings = StudioSettings.Load(Path.Combine(_dir, "studio.json"));
        settings.ProjectFolder = Path.Combine(_dir, "project");
        var vm = new ExplorerViewModel(_store, new QueryHistory(Path.Combine(_dir, "history")), settings) { Dialogs = _dialogs };
        window.DataContext = vm;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static ServerProfile Server(ExplorerViewModel vm) => vm.ServerItems.First().Server!;

    private async Task<DataDocumentViewModel> OpenDataAsync(ExplorerViewModel vm, string table)
    {
        var data = vm.OpenData(Server(vm), "main", new TableRef(table));
        await Until(() => !data.IsBusy && data.Rows.Count > 0);
        Assert.False(data.IsError, data.Status);
        return data;
    }

    private static async Task<TreeItemViewModel> ExpandToTablesAsync(ExplorerViewModel vm)
    {
        var item = vm.ServerItems.First();
        foreach (var label in new[] { "Databases", "main", "Tables" })
        {
            await item.EnsureLoadedAsync();
            item = item.Children.First(c => c.Label == label);
        }

        await item.EnsureLoadedAsync();
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

    private static void Shot(Window window, string name)
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
