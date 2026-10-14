namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Bengine.Introspection;
using Bengine.Introspection.Catalog;
using Bengine.Introspection.History;
using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Sql;
using Bengine.Introspection.Tree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>One "Go to object" hit.</summary>
/// <param name="Server">The server.</param>
/// <param name="Database">The database.</param>
/// <param name="Relation">The relation.</param>
public sealed record ObjectHit(ServerProfile Server, string Database, CatalogRelation Relation)
{
    /// <summary>Gets the list label.</summary>
    public string Label => Relation.Name;

    /// <summary>Gets the location shown beside the label.</summary>
    public string Location => $"{Server.Name} › {Database}" + (Relation.Schema is { } s ? $" › {s}" : string.Empty);

    /// <summary>Gets the icon kind.</summary>
    public string Kind => Relation.Kind == TableKind.View ? "View" : "Table";
}

/// <summary>
/// Drives the Studio window, laid out like pgAdmin with DataGrip's working
/// tools: the Object Explorer tree of registered servers (shared with the web
/// explorer through the same <see cref="ServerStore"/>), the Properties and SQL
/// tabs for the selected node, closable Data and Query Tool tabs, a metadata
/// cache per database (completion, foreign-key navigation, Go to object), tree
/// filtering, and object actions (drop, truncate, rename, SQL generator).
/// Passwords that are not saved are asked for on first connect and kept in
/// memory for the session.
/// </summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private static readonly HashSet<NodeKind> RelationKinds =
        [NodeKind.Table, NodeKind.View, NodeKind.MaterializedView, NodeKind.ForeignTable];

    private readonly ServerStore _store;
    private readonly Dictionary<string, string> _sessionPasswords = new();
    private readonly Dictionary<string, Task<DatabaseCatalog?>> _catalogs = new(StringComparer.Ordinal);
    private int _selectionVersion;

    [ObservableProperty] private TreeItemViewModel? _selectedItem;
    [ObservableProperty] private DocumentViewModel? _activeTab;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _breadcrumb = string.Empty;
    [ObservableProperty] private string _filterText = string.Empty;

    /// <summary>Creates the explorer over a server store.</summary>
    /// <param name="store">The registered-server list.</param>
    /// <param name="history">The query history, or <c>null</c> for the default location.</param>
    /// <param name="settings">The Studio settings, or <c>null</c> for the file beside the server list.</param>
    public ExplorerViewModel(ServerStore store, QueryHistory? history = null, StudioSettings? settings = null)
    {
        _store = store;
        var configDirectory = Path.GetDirectoryName(store.FilePath) ?? ".";
        History = history ?? new QueryHistory(Path.Combine(configDirectory, "history"));
        Settings = settings ?? StudioSettings.Load(Path.Combine(configDirectory, "studio.json"));
        Files = new Bengine.Introspection.Projects.SqlFiles(configDirectory);
        FilesPanel = new FilesViewModel(this, Settings.ProjectFolder);
        Bengine.Studio.Controls.Keymap.Current = new Bengine.Studio.Controls.Keymap(Settings.Shortcuts);
        QuickDoc = new PropertiesDocumentViewModel();
        Tabs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasTabs));
        ReloadServers();
    }

    /// <summary>Gets or sets the dialogs, provided by the window.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Gets the query history.</summary>
    public QueryHistory History { get; }

    /// <summary>Gets the Studio settings.</summary>
    public StudioSettings Settings { get; }

    /// <summary>Gets the SQL file store: attachments, scratches, local history.</summary>
    public Bengine.Introspection.Projects.SqlFiles Files { get; }

    /// <summary>Gets the Files panel.</summary>
    public FilesViewModel FilesPanel { get; }

    /// <summary>Gets the server groups at the root of the tree.</summary>
    public ObservableCollection<TreeItemViewModel> Roots { get; } = [];

    /// <summary>Gets the workspace tabs (data editors, consoles, DDL, tools), as in DataGrip's editor area.</summary>
    public ObservableCollection<DocumentViewModel> Tabs { get; } = [];

    /// <summary>Gets a value indicating whether any tab is open (the empty workspace shows hints).</summary>
    public bool HasTabs => Tabs.Count > 0;

    /// <summary>Gets the Quick Documentation (Ctrl+Q) of the last object asked: its properties and DDL.</summary>
    public PropertiesDocumentViewModel QuickDoc { get; }

    /// <summary>Gets whether any server is registered.</summary>
    public bool HasServers => Roots.Count > 0;

    /// <summary>Gets the file the server list is stored in.</summary>
    public string StorePath => _store.FilePath;

    /// <summary>Gets every server row.</summary>
    public IEnumerable<TreeItemViewModel> ServerItems => Roots.SelectMany(r => r.Kind == "Server" ? [r] : r.Children.AsEnumerable());

    /// <summary>
    /// Reloads the server list from the store, rebuilding the tree: data sources
    /// at the root, as in DataGrip, and folders only for named groups.
    /// </summary>
    public void ReloadServers()
    {
        Roots.Clear();
        var groups = _store.List().GroupBy(s => s.Group).ToList();
        foreach (var group in groups.Where(g => !IsDefaultGroup(g.Key)))
        {
            var row = TreeItemViewModel.ForGroup(this, group.Key, group.Count());
            foreach (var server in group)
            {
                row.Children.Add(TreeItemViewModel.ForServer(this, server, row));
            }

            row.IsExpanded = true;
            Roots.Add(row);
        }

        foreach (var server in groups.Where(g => IsDefaultGroup(g.Key)).SelectMany(g => g))
        {
            Roots.Add(TreeItemViewModel.ForServer(this, server, null));
        }

        OnPropertyChanged(nameof(HasServers));
    }

    private static bool IsDefaultGroup(string? group) => string.IsNullOrWhiteSpace(group) || group == "Servers";

    /// <summary>Gets the password to use for a server: one typed this session, else the saved one.</summary>
    /// <param name="server">The server.</param>
    /// <returns>The password, or <c>null</c>.</returns>
    public string? PasswordFor(ServerProfile server) =>
        _sessionPasswords.TryGetValue(server.Id, out var typed) ? typed : server.Password;

    /// <summary>Records that a server accepted our credentials.</summary>
    /// <param name="server">The server.</param>
    public void MarkConnected(ServerProfile server) => Status = $"Connected to {server.Name} ({server.Endpoint}).";

    /// <summary>
    /// Loads a node's children, asking for a password whenever the server refuses
    /// the login. Returns <c>null</c> if the user cancels the prompt.
    /// </summary>
    /// <param name="server">The server.</param>
    /// <param name="node">The node to expand.</param>
    /// <returns>The children, or <c>null</c> if cancelled.</returns>
    public async Task<IReadOnlyList<TreeNode>?> FetchChildrenAsync(ServerProfile server, NodeRef node)
    {
        while (true)
        {
            try
            {
                return await ObjectExplorer.GetChildrenAsync(server, PasswordFor(server), node);
            }
            catch (IntrospectionException ex) when (ObjectExplorer.IsAuthenticationError(ex))
            {
                if (!await SignInAsync(server))
                {
                    return null;
                }
            }
        }
    }

    /// <summary>Prompts for a server's password until it connects or the user cancels.</summary>
    /// <param name="server">The server.</param>
    /// <returns><c>true</c> once connected.</returns>
    public async Task<bool> SignInAsync(ServerProfile server)
    {
        if (Dialogs is null)
        {
            return false;
        }

        string? error = null;
        while (true)
        {
            var answer = await Dialogs.AskPasswordAsync(server, error);
            if (answer is null)
            {
                return false;
            }

            try
            {
                await ObjectExplorer.TestAsync(server, answer.Password);
                _sessionPasswords[server.Id] = answer.Password;
                if (answer.Save)
                {
                    _store.Upsert(server with { Password = answer.Password, SavePassword = true });
                }

                return true;
            }
            catch (IntrospectionException ex)
            {
                error = ObjectExplorer.IsAuthenticationError(ex) ? "Password authentication failed." : ex.Message;
            }
        }
    }

    /// <summary>Opens a session, asking for the password when the server refuses the login.</summary>
    /// <param name="server">The server.</param>
    /// <param name="database">The database, or <c>null</c> for the maintenance database.</param>
    /// <returns>The session.</returns>
    /// <exception cref="IntrospectionException">The connection failed or the sign-in was cancelled.</exception>
    public async Task<DatabaseSession> OpenSessionAsync(ServerProfile server, string? database)
    {
        while (true)
        {
            try
            {
                return await ObjectExplorer.OpenSessionAsync(server, PasswordFor(server), database);
            }
            catch (IntrospectionException ex) when (ObjectExplorer.IsAuthenticationError(ex))
            {
                if (!await SignInAsync(server))
                {
                    throw new IntrospectionException($"Not connected to {server.Name}: sign-in was cancelled.");
                }
            }
        }
    }

    /// <summary>Gets a database's metadata from the cache, reading it on first use or on refresh.</summary>
    /// <param name="server">The server.</param>
    /// <param name="database">The database, or <c>null</c> for the maintenance database.</param>
    /// <param name="refresh">Whether to re-read it.</param>
    /// <returns>The catalog, or <c>null</c> when it could not be read.</returns>
    public Task<DatabaseCatalog?> GetCatalogAsync(ServerProfile server, string? database, bool refresh = false)
    {
        var key = $"{server.Id}|{database ?? server.MaintenanceDatabase}";
        if (refresh || !_catalogs.TryGetValue(key, out var task) || task.IsFaulted || (task.IsCompleted && task.Result is null))
        {
            task = ReadCatalogAsync(server, database);
            _catalogs[key] = task;
        }

        return task;
    }

    /// <summary>Opens a View/Edit Data tab for a relation node, or focuses the existing one.</summary>
    /// <param name="item">The relation's tree row.</param>
    public void OpenData(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node is not { } node || !RelationKinds.Contains(node.Ref.Kind))
        {
            return;
        }

        var kind = node.Ref.Kind == NodeKind.Table ? TableKind.Table : TableKind.View;
        OpenData(server, node.Ref.Database, new TableRef(node.Ref.Name!, node.Ref.Schema, kind));
    }

    /// <summary>Opens a View/Edit Data tab for a relation, optionally filtered.</summary>
    /// <param name="server">The server.</param>
    /// <param name="database">The database.</param>
    /// <param name="table">The relation.</param>
    /// <param name="where">An initial <c>WHERE</c> condition, or <c>null</c>.</param>
    /// <returns>The tab.</returns>
    public DataDocumentViewModel OpenData(ServerProfile server, string? database, TableRef table, string? where = null)
    {
        var existing = Tabs.OfType<DataDocumentViewModel>()
            .FirstOrDefault(d => d.Server.Id == server.Id && d.Location == $"{server.Name} / {database}"
                && d.Table.Name == table.Name && d.Table.Schema == table.Schema);
        if (existing is not null && where is null)
        {
            ActiveTab = existing;
            return existing;
        }

        var context = new DataContext(
            server,
            database,
            () => OpenSessionAsync(server, database),
            refresh => GetCatalogAsync(server, database, refresh),
            (target, condition) => OpenData(server, database, target, condition),
            sql => _ = OpenQueryAsync(server, database, sql),
            Dialogs);
        var doc = new DataDocumentViewModel(context, table, where);
        AddTab(doc);
        _ = doc.InitializeAsync();
        return doc;
    }

    /// <summary>Opens a Query Tool on a database of a server.</summary>
    /// <param name="server">The server.</param>
    /// <param name="database">The database, or <c>null</c> for the maintenance database.</param>
    /// <param name="sql">Initial SQL, if any.</param>
    /// <param name="filePath">The SQL file the tab edits, or <c>null</c> for a console.</param>
    /// <returns>The tab, or <c>null</c> when the sign-in was cancelled.</returns>
    public async Task<QueryDocumentViewModel?> OpenQueryAsync(ServerProfile server, string? database, string? sql = null, string? filePath = null)
    {
        if (!await EnsureConnectedAsync(server))
        {
            return null;
        }

        var db = database ?? server.MaintenanceDatabase ?? (server.Provider == Bengine.Enums.DatabaseProvider.Sqlite ? "main" : null);
        var context = new QueryContext(
            server,
            name => OpenSessionAsync(server, name),
            (name, refresh) => GetCatalogAsync(server, name, refresh),
            () => ListDatabasesAsync(server),
            History,
            Dialogs,
            Files);
        var doc = new QueryDocumentViewModel(context, db, sql, filePath);
        AddTab(doc);
        _ = doc.InitializeAsync();
        return doc;
    }

    /// <summary>
    /// Opens a SQL file in a Query Tool bound to it, on the data source the file
    /// is attached to (else the selected server, else the first one).
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The tab, or <c>null</c> when no server can run it.</returns>
    public async Task<QueryDocumentViewModel?> OpenFileAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (Tabs.OfType<QueryDocumentViewModel>().FirstOrDefault(q => q.FilePath == path) is { } open)
        {
            ActiveTab = open;
            return open;
        }

        var target = Files.TargetOf(path);
        var server = (target is not null ? _store.Get(target.ServerId) : null) ?? SelectedItem?.Server ?? _store.List().FirstOrDefault();
        if (server is null)
        {
            Status = "Register a server to run SQL files.";
            return null;
        }

        var database = target?.ServerId == server.Id ? target.Database
            : SelectedItem?.Server?.Id == server.Id ? SelectedItem.Node?.Ref.Database : null;
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Could not open {Path.GetFileName(path)}: {ex.Message}";
            return null;
        }

        var doc = await OpenQueryAsync(server, database, text, path);
        if (doc is not null && target is null)
        {
            Files.Attach(path, new Bengine.Introspection.Projects.FileTarget(server.Id, doc.Database));
        }

        return doc;
    }

    /// <summary>Searches the cached metadata of every opened database for relations matching a text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The hits, best first.</returns>
    public async Task<IReadOnlyList<ObjectHit>> SearchObjectsAsync(string text)
    {
        var hits = new List<(ObjectHit Hit, int Score)>();
        foreach (var (key, task) in _catalogs.ToList())
        {
            if (!task.IsCompletedSuccessfully || await task is not { } catalog)
            {
                continue;
            }

            var server = _store.Get(key.Split('|')[0]);
            if (server is null)
            {
                continue;
            }

            foreach (var relation in catalog.Relations)
            {
                var score = SqlCompletion.MatchScore(relation.Name, text);
                if (score > 0)
                {
                    hits.Add((new ObjectHit(server, key.Split('|', 2)[1], relation), score));
                }
            }
        }

        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Hit.Relation.Name.Length).ThenBy(h => h.Hit.Relation.Name)
            .Select(h => h.Hit).Take(200).ToList();
    }

    /// <summary>Loads the metadata of every database of the connected servers, for Go to object.</summary>
    /// <returns>A task that completes when loaded.</returns>
    public async Task WarmCatalogsAsync()
    {
        foreach (var serverItem in ServerItems.Where(s => s.IsConnected && s.Server is not null))
        {
            var server = serverItem.Server!;
            IReadOnlyList<string> databases;
            try
            {
                databases = await ListDatabasesAsync(server);
            }
            catch (IntrospectionException)
            {
                continue;
            }

            await Task.WhenAll(databases.Take(30).Select(db => GetCatalogAsync(server, db)));
        }
    }

    /// <summary>Expands the tree down to a relation and selects it.</summary>
    /// <param name="hit">The relation.</param>
    /// <returns>The row, or <c>null</c> when it could not be found.</returns>
    public async Task<TreeItemViewModel?> RevealAsync(ObjectHit hit)
    {
        var current = ServerItems.FirstOrDefault(s => s.Server?.Id == hit.Server.Id);
        var kind = hit.Relation.Kind == TableKind.View ? NodeKind.View : NodeKind.Table;
        for (var depth = 0; current is not null && depth < 10; depth++)
        {
            if (!await current.EnsureLoadedAsync())
            {
                return null;
            }

            var target = current.Children.FirstOrDefault(c => c.Node?.Ref is { } r
                && (r.Kind == kind || (kind == NodeKind.View && r.Kind == NodeKind.MaterializedView))
                && r.Name == hit.Relation.Name
                && (r.Schema is null || hit.Relation.Schema is null || r.Schema == hit.Relation.Schema));
            if (target is not null)
            {
                SelectedItem = target;
                return target;
            }

            var databases = current.Children.Count(x => x.Node?.Ref.Kind == NodeKind.Database);
            current = current.Children.FirstOrDefault(c => c.Node?.Ref is { } r && r.Kind switch
            {
                NodeKind.Database => r.Name == hit.Database || databases == 1,
                NodeKind.Schema => r.Name == hit.Relation.Schema,
                NodeKind.TablesFolder => kind == NodeKind.Table,
                NodeKind.ViewsFolder => kind == NodeKind.View,
                _ => false,
            });
        }

        return null;
    }

    /// <summary>Gets whether a node can be dropped.</summary>
    /// <param name="item">The row.</param>
    /// <returns><c>true</c> if a Drop action applies.</returns>
    public static bool CanDrop(TreeItemViewModel item) => item.Node is { } node && !node.Ref.IsFolder && item.Server is { } server && ObjectSql.CanDrop(server.Provider, node.Ref.Kind);

    /// <summary>Gets whether a node can be renamed.</summary>
    /// <param name="item">The row.</param>
    /// <returns><c>true</c> if a Rename action applies.</returns>
    public static bool CanRename(TreeItemViewModel item) => item.Node is { } node && !node.Ref.IsFolder && item.Server is { } server && ObjectSql.CanRename(server.Provider, node.Ref.Kind);

    /// <summary>Drops an object after showing its statement.</summary>
    /// <param name="item">The row.</param>
    /// <param name="cascade">Whether to drop dependents (PostgreSQL).</param>
    /// <returns>A task that completes when done.</returns>
    public Task DropAsync(TreeItemViewModel item, bool cascade = false) =>
        RunObjectSqlAsync(item, $"Drop {item.Kind}", ObjectSql.Drop(item.Server!.Provider, item.Node!.Ref, cascade), "Drop", refreshParent: true);

    /// <summary>Empties a table after confirmation.</summary>
    /// <param name="item">The row.</param>
    /// <returns>A task that completes when done.</returns>
    public Task TruncateAsync(TreeItemViewModel item) =>
        RunObjectSqlAsync(
            item,
            item.Server!.Provider == Bengine.Enums.DatabaseProvider.MongoDb ? "Delete All Documents" : "Truncate Table",
            ObjectSql.Truncate(item.Server.Provider, item.Node!.Ref),
            item.Server.Provider == Bengine.Enums.DatabaseProvider.MongoDb ? "Delete" : "Truncate",
            refreshParent: false);

    /// <summary>Renames an object.</summary>
    /// <param name="item">The row.</param>
    /// <returns>A task that completes when done.</returns>
    public async Task RenameAsync(TreeItemViewModel item)
    {
        if (Dialogs is null || item.Node?.Ref.Name is not { } name
            || await Dialogs.PromptAsync($"Rename {item.Kind}", $"New name for {name}:", name) is not { Length: > 0 } newName || newName == name)
        {
            return;
        }

        string sql;
        var server = item.Server!;
        newName = newName.Trim();
        try
        {
            sql = ObjectSql.Rename(server.Provider, item.Node.Ref, newName);
        }
        catch (NotSupportedException ex)
        {
            Status = ex.Message;
            return;
        }

        // Definitions that mention the object are listed in the review: the rename does not edit them
        // (PostgreSQL views follow it; routine and trigger bodies, and MySQL views, do not).
        var usages = CanFindUsages(item) ? await FindUsagesAsync(item) : [];
        var inDatabase = usages.Where(u => u.Kind is not ("File" or "Console")).ToList();
        if (inDatabase.Count > 0)
        {
            sql = $"-- {name} is mentioned in these definitions; check them after the rename:\n"
                + string.Join('\n', inDatabase.Select(u => $"--   {u.Kind} {u.Name}")) + "\n\n" + sql;
        }

        if (!await RunObjectSqlAsync(item, $"Rename {item.Kind}", sql, "Rename", refreshParent: true))
        {
            return;
        }

        var inText = usages.Where(u => u.Kind is "File" or "Console").ToList();
        if (inText.Count > 0 && await Dialogs.ConfirmAsync(
                "Update References",
                $"Also rename {name} to {newName} in {inText.Select(u => u.Name).Distinct().Count()} open console(s) and SQL file(s)?"))
        {
            var count = RenameInText(server, name, newName);
            Status = $"Renamed {name} to {newName}; updated {count} reference(s) in SQL text.";
        }
    }

    /// <summary>Opens the Dump / Restore dialog for a database and refreshes it after a restore.</summary>
    /// <param name="item">The database row.</param>
    /// <param name="restore">Whether to restore (else dump).</param>
    /// <returns>A task that completes when the dialog closes.</returns>
    public async Task BackupAsync(TreeItemViewModel item, bool restore)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { } r || Dialogs is null)
        {
            return;
        }

        var database = r.Kind == NodeKind.Database ? r.Name : r.Database;
        database ??= server.Provider == Bengine.Enums.DatabaseProvider.Sqlite ? "main" : server.MaintenanceDatabase;
        if (database is null || !await EnsureConnectedAsync(server))
        {
            return;
        }

        var backup = new BackupViewModel(
            server,
            database,
            restore,
            () => OpenSessionAsync(server, database),
            () => OpenSessionAsync(server, null),
            PasswordFor(server),
            Dialogs,
            Settings);
        await Dialogs.ShowBackupAsync(backup);
        if (restore)
        {
            _catalogs.Remove($"{server.Id}|{database}");
            await item.RefreshAsync();
        }
    }

    /// <summary>Gets whether Find Usages applies to a node (relations, routines, sequences, types).</summary>
    /// <param name="item">The row.</param>
    /// <returns><c>true</c> if usages can be searched.</returns>
    public static bool CanFindUsages(TreeItemViewModel item) => item.Server?.Provider != Bengine.Enums.DatabaseProvider.MongoDb && item.Node?.Ref.Kind is NodeKind.Table or NodeKind.View or NodeKind.MaterializedView
        or NodeKind.ForeignTable or NodeKind.Function or NodeKind.Procedure or NodeKind.TriggerFunction or NodeKind.Sequence or NodeKind.Type or NodeKind.Domain;

    /// <summary>
    /// Finds where an object is used: in the definitions of views, routines, and
    /// triggers of its database, in the server's open Query Tools, and in the
    /// project's SQL files (those attached to the server, or to nothing).
    /// </summary>
    /// <param name="item">The object's row.</param>
    /// <returns>The usages.</returns>
    public async Task<IReadOnlyList<Usage>> FindUsagesAsync(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { Name: { } name } r)
        {
            return [];
        }

        var usages = new List<Usage>();
        try
        {
            await using var session = await OpenSessionAsync(server, r.Database);
            var self = r.Schema is null ? name : $"{r.Schema}.{name}";
            usages.AddRange((await ObjectUsages.InDatabaseAsync(session, name)).Where(u => !u.Name.Equals(self, StringComparison.OrdinalIgnoreCase)));
        }
        catch (IntrospectionException ex)
        {
            Status = $"Could not search the database: {ex.Message}";
        }

        var open = Tabs.OfType<QueryDocumentViewModel>().Where(q => q.Server.Id == server.Id).ToList();
        foreach (var tab in open)
        {
            usages.AddRange(ObjectUsages.InText(tab.FilePath is null ? "Console" : "File", tab.FilePath ?? tab.Title, tab.QueryText, name, server.Provider));
        }

        foreach (var file in ProjectFiles(server).Where(f => open.All(q => q.FilePath != f)))
        {
            var text = await File.ReadAllTextAsync(file);
            if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                usages.AddRange(ObjectUsages.InText("File", file, text, name, server.Provider));
            }
        }

        return usages;
    }

    /// <summary>Shows the usages of an object (Alt+F7) and opens the chosen one.</summary>
    /// <param name="item">The object's row.</param>
    /// <returns>A task that completes when the dialog closes.</returns>
    public async Task ShowUsagesAsync(TreeItemViewModel item)
    {
        if (Dialogs is null || item.Server is not { } server || item.Node?.Ref.Name is not { } name)
        {
            return;
        }

        Status = $"Searching usages of {name}…";
        var usages = await FindUsagesAsync(item);
        Status = $"{usages.Count} usage(s) of {name}.";
        if (await Dialogs.ShowUsagesAsync($"Usages of {name}", usages) is { } usage)
        {
            await OpenUsageAsync(server, usage);
        }
    }

    /// <summary>Opens a usage: the file or console at its line, or a database object's definition.</summary>
    /// <param name="server">The server searched.</param>
    /// <param name="usage">The usage.</param>
    /// <returns>A task that completes when shown.</returns>
    public async Task OpenUsageAsync(ServerProfile server, Usage usage)
    {
        QueryDocumentViewModel? tab = usage.Kind switch
        {
            "File" => await OpenFileAsync(usage.Name),
            "Console" => Tabs.OfType<QueryDocumentViewModel>().FirstOrDefault(q => q.Server.Id == server.Id && q.Title == usage.Name),
            _ => null,
        };
        if (tab is not null)
        {
            ActiveTab = tab;
            tab.GoToLine(usage.Line ?? 1);
        }
        else if (usage.Definition is { } definition && Dialogs is not null)
        {
            await Dialogs.ReviewSqlAsync($"{usage.Kind} {usage.Name}", definition, null);
        }
    }

    // Renames identifier references in the server's open Query Tools and its project files.
    private int RenameInText(ServerProfile server, string oldName, string newName)
    {
        var count = 0;
        var open = Tabs.OfType<QueryDocumentViewModel>().Where(q => q.Server.Id == server.Id).ToList();
        foreach (var tab in open)
        {
            var (text, n) = ObjectUsages.RenameInText(tab.QueryText, oldName, newName, server.Provider);
            if (n > 0)
            {
                tab.QueryText = text;
                count += n;
            }
        }

        foreach (var file in ProjectFiles(server).Where(f => open.All(q => q.FilePath != f)))
        {
            var (text, n) = ObjectUsages.RenameInText(File.ReadAllText(file), oldName, newName, server.Provider);
            if (n > 0)
            {
                Files.Save(file, text);
                count += n;
            }
        }

        return count;
    }

    // The project's SQL files (and scratches) attached to a server, or to nothing.
    private IEnumerable<string> ProjectFiles(ServerProfile server)
    {
        IEnumerable<string> In(string folder) => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.sql", SearchOption.AllDirectories).Take(2000)
            : [];

        return In(Settings.ProjectFolder).Concat(In(Files.ScratchDirectory))
            .Select(Path.GetFullPath)
            .Distinct()
            .Where(f => Files.TargetOf(f) is not { } target || target.ServerId == server.Id);
    }

    /// <summary>Opens the table editor on a table and refreshes the tree when it changed.</summary>
    /// <param name="item">The table row.</param>
    /// <returns>A task that completes when the editor closes.</returns>
    public async Task ModifyTableAsync(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { Kind: NodeKind.Table } r || Dialogs is null)
        {
            return;
        }

        try
        {
            Bengine.Models.Table table;
            await using (var session = await OpenSessionAsync(server, r.Database))
            {
                table = await session.Introspector.IntrospectTableAsync(new TableRef(r.Name!, r.Schema));
            }

            var editor = new TableEditorViewModel(Bengine.Introspection.Design.TableDesign.From(table, server.Provider), () => OpenSessionAsync(server, r.Database), Dialogs, server.ReadOnly);
            if (await Dialogs.EditTableAsync(editor))
            {
                Status = $"Table {editor.Design.Name} changed.";
                await (item.Parent?.RefreshAsync() ?? Task.CompletedTask);
                _ = GetCatalogAsync(server, r.Database, refresh: true);
            }
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
        }
    }

    /// <summary>Opens the table editor for a new table in a schema or database.</summary>
    /// <param name="item">The schema, database, or Tables folder row.</param>
    /// <returns>A task that completes when the editor closes.</returns>
    public async Task NewTableAsync(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { } r || Dialogs is null)
        {
            return;
        }

        var schema = r.Schema ?? server.Provider switch
        {
            Bengine.Enums.DatabaseProvider.PostgreSql => "public",
            Bengine.Enums.DatabaseProvider.SqlServer => "dbo",
            Bengine.Enums.DatabaseProvider.MySql => r.Database,
            _ => null,
        };
        var design = new Bengine.Introspection.Design.TableDesign(server.Provider, schema) { Name = "new_table" };
        design.Columns.Add(new Bengine.Introspection.Design.ColumnDesign
        {
            Name = "id",
            Type = server.Provider switch
            {
                Bengine.Enums.DatabaseProvider.Sqlite => "INTEGER",
                Bengine.Enums.DatabaseProvider.MySql or Bengine.Enums.DatabaseProvider.SqlServer => "int",
                Bengine.Enums.DatabaseProvider.Oracle => "NUMBER(19)",
                _ => "bigint",
            },
            PrimaryKey = true,
            Identity = true,
            Nullable = false,
        });
        var editor = new TableEditorViewModel(design, () => OpenSessionAsync(server, r.Database), Dialogs, server.ReadOnly);
        if (await Dialogs.EditTableAsync(editor))
        {
            Status = $"Table {design.Name} created.";
            var folder = item.Kind == "TablesFolder" ? item : item.Children.FirstOrDefault(c => c.Kind == "TablesFolder")
                ?? item.Children.SelectMany(c => c.Children).FirstOrDefault(c => c.Kind == "TablesFolder");
            await (folder?.RefreshAsync() ?? item.RefreshAsync());
            _ = GetCatalogAsync(server, r.Database, refresh: true);
        }
    }

    /// <summary>Creates a MongoDB collection in a database, asking for its name.</summary>
    /// <param name="item">The database or Collections folder row.</param>
    /// <returns>A task that completes when done.</returns>
    public async Task NewCollectionAsync(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { Database: { } database } || Dialogs is null)
        {
            return;
        }

        if (server.ReadOnly)
        {
            Status = $"\"{server.Name}\" is read-only.";
            return;
        }

        if (await Dialogs.PromptAsync("New Collection", $"Name of the new collection in {database}:", "new_collection") is not { Length: > 0 } name)
        {
            return;
        }

        try
        {
            await using var session = await OpenSessionAsync(server, database);
            await session.Query.ExecuteAllAsync($"db.createCollection({Bengine.Introspection.Mongo.MongoValues.Quote(name.Trim())})");
            Status = $"Collection {name.Trim()} created.";
            var folder = item.Kind == "TablesFolder" ? item : item.Children.FirstOrDefault(c => c.Kind == "TablesFolder");
            await (folder?.RefreshAsync() ?? item.RefreshAsync());
            _ = GetCatalogAsync(server, database, refresh: true);
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
        }
    }

    /// <summary>Imports a delimited file into a table (an existing one, or a new one).</summary>
    /// <param name="item">A table row (import into it) or a schema/database/Tables row (create a table).</param>
    /// <returns>A task that completes when the dialog closes.</returns>
    public async Task ImportDataAsync(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { } r || Dialogs is null)
        {
            return;
        }

        var catalog = await GetCatalogAsync(server, r.Database);
        var schema = r.Schema ?? server.Provider switch
        {
            Bengine.Enums.DatabaseProvider.PostgreSql => "public",
            Bengine.Enums.DatabaseProvider.SqlServer => "dbo",
            Bengine.Enums.DatabaseProvider.MySql => r.Database,
            _ => null,
        };
        var tables = catalog?.Relations.Where(t => schema is null || t.Schema is null || string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        var target = r.Kind == NodeKind.Table ? catalog?.Find(r.Schema, r.Name!) : null;
        var import = new ImportDataViewModel(server.Provider, schema, tables, target, () => OpenSessionAsync(server, r.Database), Dialogs);
        if (await Dialogs.ImportDataAsync(import) && import.Imported is { } table)
        {
            Status = import.Status ?? "Imported.";
            _ = GetCatalogAsync(server, r.Database, refresh: true);
            if (r.Kind != NodeKind.Table)
            {
                var folder = item.Kind == "TablesFolder" ? item : item.Children.FirstOrDefault(c => c.Kind == "TablesFolder")
                    ?? item.Children.SelectMany(c => c.Children).FirstOrDefault(c => c.Kind == "TablesFolder");
                await (folder?.RefreshAsync() ?? Task.CompletedTask);
            }

            OpenData(server, r.Database, table);
        }
    }

    /// <summary>Opens a schema comparison, with the source preset from a database or schema node.</summary>
    /// <param name="item">The node, or <c>null</c>.</param>
    /// <returns>The tab.</returns>
    public async Task<SchemaCompareViewModel> OpenCompareAsync(TreeItemViewModel? item = null)
    {
        // Comparisons are relational; MongoDB has no schema to diff.
        var compare = new SchemaCompareViewModel(this, _store.List().Where(s => s.Provider != Bengine.Enums.DatabaseProvider.MongoDb).ToList());
        AddTab(compare);
        if (item is { Server: { Provider: not Bengine.Enums.DatabaseProvider.MongoDb } server, Node.Ref: var r })
        {
            await compare.Source.SelectAsync(server, r.Database, r.Kind == NodeKind.Schema ? r.Name : null);
            await compare.Target.SelectAsync(server, r.Database, r.Kind == NodeKind.Schema ? r.Name : null);
        }

        return compare;
    }

    /// <summary>Opens a Data Compare tab, with a table as both source and target to start from.</summary>
    /// <param name="item">A table row, or <c>null</c>.</param>
    /// <returns>The tab.</returns>
    public async Task<DataCompareViewModel> OpenDataCompareAsync(TreeItemViewModel? item = null)
    {
        var compare = new DataCompareViewModel(this, _store.List().Where(s => s.Provider != Bengine.Enums.DatabaseProvider.MongoDb).ToList());
        AddTab(compare);
        if (item is { Server: { Provider: not Bengine.Enums.DatabaseProvider.MongoDb } server, Node.Ref: { Name: { } name } r })
        {
            await compare.Source.SelectAsync(server, r.Database, r.Schema, name);
            await compare.Target.SelectAsync(server, r.Database, r.Schema, name);
        }

        return compare;
    }

    /// <summary>Opens a server's activity view.</summary>
    /// <param name="server">The server.</param>
    /// <returns>The tab.</returns>
    public ActivityDocumentViewModel OpenActivity(ServerProfile server)
    {
        var existing = Tabs.OfType<ActivityDocumentViewModel>().FirstOrDefault(a => a.Server.Id == server.Id);
        if (existing is not null)
        {
            ActiveTab = existing;
            return existing;
        }

        var activity = new ActivityDocumentViewModel(this, server);
        AddTab(activity);
        _ = activity.InitializeAsync();
        return activity;
    }

    /// <summary>Opens an ER diagram of a database or schema.</summary>
    /// <param name="item">The database or schema row.</param>
    /// <returns>The tab, or <c>null</c>.</returns>
    public DiagramDocumentViewModel? OpenDiagram(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { } r)
        {
            return null;
        }

        var schema = r.Kind == NodeKind.Schema ? r.Name : server.Provider == Bengine.Enums.DatabaseProvider.PostgreSql ? "public" : null;
        var diagram = new DiagramDocumentViewModel(this, server, r.Database, schema);
        AddTab(diagram);
        _ = diagram.InitializeAsync();
        return diagram;
    }

    /// <summary>Opens a Query Tool with a generated statement for a relation.</summary>
    /// <param name="item">The relation row.</param>
    /// <param name="kind">select, insert, update, delete, or ddl.</param>
    /// <returns>A task that completes when the tab is open.</returns>
    public async Task GenerateSqlAsync(TreeItemViewModel item, string kind)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { } r)
        {
            return;
        }

        string? sql;
        if (kind == "ddl")
        {
            sql = (await ObjectExplorer.GetPropertiesAsync(server, PasswordFor(server), r)).Sql;
        }
        else
        {
            var catalog = await GetCatalogAsync(server, r.Database);
            sql = catalog?.Find(r.Schema, r.Name!) is { } relation ? ObjectSql.Template(server.Provider, relation, kind) : null;
        }

        if (sql is null)
        {
            Status = "Nothing to generate for this object.";
            return;
        }

        await OpenQueryAsync(server, r.Database, sql);
    }

    /// <summary>Applies the tree filter: rows whose label contains the text stay visible with their ancestors.</summary>
    /// <param name="value">The filter text.</param>
    partial void OnFilterTextChanged(string value)
    {
        foreach (var root in Roots)
        {
            root.ApplyFilter(value.Trim());
        }
    }

    [RelayCommand]
    private async Task RegisterServerAsync()
    {
        if (Dialogs is not null && await Dialogs.EditServerAsync(null) is not null)
        {
            ReloadServers();
        }
    }

    [RelayCommand]
    private async Task ImportPgAdminAsync()
    {
        if (Dialogs is null)
        {
            return;
        }

        var count = await Dialogs.ImportFromPgAdminAsync();
        if (count > 0)
        {
            ReloadServers();
            Status = $"Imported {count} server(s).";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedItem is { Node: not null } item)
        {
            await item.RefreshAsync();
            if (item.Server is { } server && item.Node.Ref.Database is { } db)
            {
                _ = GetCatalogAsync(server, db, refresh: true);
            }
        }
        else
        {
            ReloadServers();
        }
    }

    [RelayCommand]
    private Task QueryToolAsync()
    {
        if (SelectedItem is not { Server: { } server } item)
        {
            return Task.CompletedTask;
        }

        return OpenQueryAsync(server, item.Node?.Ref.Database);
    }

    [RelayCommand]
    private async Task OpenSqlFileAsync()
    {
        if (Dialogs is not null && await Dialogs.OpenFileAsync("Open SQL File", ["sql", "ddl"]) is { } path)
        {
            await OpenFileAsync(path);
        }
    }

    [RelayCommand]
    private Task CompareAsync() => OpenCompareAsync(SelectedItem is { Kind: "Database" or "Schema" } item ? item : null);

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        if (Dialogs is not null && await Dialogs.ShowHistoryAsync(History, null) is { } entry
            && _store.Get(entry.ServerId) is { } server)
        {
            await OpenQueryAsync(server, entry.Database, entry.Sql);
        }
    }

    /// <summary>Opens the Properties dialog for a server row and reloads the tree on save.</summary>
    /// <param name="item">The server row.</param>
    /// <returns>A task that completes when the dialog closes.</returns>
    public async Task EditServerAsync(TreeItemViewModel item)
    {
        if (Dialogs is not null && item.Server is { } server && await Dialogs.EditServerAsync(server) is not null)
        {
            _sessionPasswords.Remove(server.Id);
            ReloadServers();
        }
    }

    /// <summary>Removes a server registration after confirmation.</summary>
    /// <param name="item">The server row.</param>
    /// <returns>A task that completes when done.</returns>
    public async Task RemoveServerAsync(TreeItemViewModel item)
    {
        if (item.Server is not { } server || Dialogs is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync("Remove Server", $"Remove \"{server.Name}\" from the list? The database itself is not touched."))
        {
            return;
        }

        _store.Remove(server.Id);
        _sessionPasswords.Remove(server.Id);
        foreach (var tab in Tabs.Where(t => t is DataDocumentViewModel d && d.Server.Id == server.Id || t is QueryDocumentViewModel q && q.Server.Id == server.Id).ToList())
        {
            await CloseTabAsync(tab);
        }

        ActiveTab = Tabs.FirstOrDefault();
        ReloadServers();
    }

    /// <summary>Disconnects a server: forgets the session password and collapses its tree.</summary>
    /// <param name="item">The server row.</param>
    public void Disconnect(TreeItemViewModel item)
    {
        if (item.Server is not { } server)
        {
            return;
        }

        _sessionPasswords.Remove(server.Id);
        ObjectExplorer.Disconnect(server.Id);
        foreach (var key in _catalogs.Keys.Where(k => k.StartsWith(server.Id + "|", StringComparison.Ordinal)).ToList())
        {
            _catalogs.Remove(key);
        }

        item.Reset();
        Status = $"Disconnected from {server.Name}.";
    }

    /// <summary>Closes a tab, letting a Query Tool settle an open transaction first.</summary>
    /// <param name="doc">The tab.</param>
    /// <returns>A task that completes when the tab is closed.</returns>
    public async Task CloseTabAsync(DocumentViewModel doc)
    {
        if (!doc.CanClose)
        {
            return;
        }

        if (doc is QueryDocumentViewModel query)
        {
            if (!await query.CanCloseAsync())
            {
                return;
            }

            await query.DisposeAsync();
        }
        else if (doc is ActivityDocumentViewModel activity)
        {
            await activity.DisposeAsync();
        }
        else if (doc is DataDocumentViewModel { HasPending: true } data && Dialogs is not null
                 && !await Dialogs.ConfirmAsync("Pending Changes", $"Close {data.Table.Name} and discard {data.PendingCount} pending change(s)?"))
        {
            return;
        }

        var index = Tabs.IndexOf(doc);
        Tabs.Remove(doc);
        if (ActiveTab == doc || ActiveTab is null)
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
        }
    }

    // Selecting a row only selects it, as in DataGrip; Ctrl+Q, F4, and Ctrl+B open what it holds.
    partial void OnSelectedItemChanged(TreeItemViewModel? value) =>
        Breadcrumb = value is null || value.IsPlaceholder ? string.Empty : string.Join("  ›  ", value.Breadcrumb());

    private async Task<bool> RunObjectSqlAsync(TreeItemViewModel item, string title, string sql, string verb, bool refreshParent)
    {
        if (item.Server is not { } server || Dialogs is null)
        {
            return false;
        }

        if (server.ReadOnly)
        {
            Status = $"\"{server.Name}\" is read-only.";
            return false;
        }

        if (!await Dialogs.ReviewSqlAsync(title, sql, verb))
        {
            return false;
        }

        var database = item.Node!.Ref.Kind == NodeKind.Database ? null : item.Node.Ref.Database;
        try
        {
            await using var session = await OpenSessionAsync(server, database);
            await session.Query.ExecuteAllAsync(sql);
            Status = $"{verb}: done.";
            if (refreshParent && item.Parent is { } parent)
            {
                await parent.RefreshAsync();
            }

            _ = GetCatalogAsync(server, item.Node.Ref.Database, refresh: true);
            return true;
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            await Dialogs.ReviewSqlAsync($"{verb} failed", $"-- {ex.Message}\n\n{sql}", null);
            return false;
        }
    }

    private async Task<DatabaseCatalog?> ReadCatalogAsync(ServerProfile server, string? database)
    {
        try
        {
            await using var session = await OpenSessionAsync(server, database);
            return await session.ReadCatalogAsync();
        }
        catch (IntrospectionException)
        {
            return null;
        }
    }

    /// <summary>Lists a server's databases (those that accept connections).</summary>
    /// <param name="server">The server.</param>
    /// <returns>The database names.</returns>
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(ServerProfile server)
    {
        var databases = await FetchChildrenAsync(server, new NodeRef { Kind = NodeKind.DatabasesFolder });
        return databases?.Where(d => d.Ref.Kind == NodeKind.Database && d.HasChildren).Select(d => d.Ref.Name!).ToList() ?? [];
    }

    /// <summary>Loads an object's Quick Documentation (Ctrl+Q): its properties, or its children's summary, and its DDL.</summary>
    /// <param name="item">The row.</param>
    /// <returns>A task that completes when loaded.</returns>
    public async Task LoadQuickDocAsync(TreeItemViewModel? item)
    {
        var version = ++_selectionVersion;
        var doc = QuickDoc;
        doc.Rows.Clear();
        doc.Sql = null;
        if (item is not { Server: { } server, Node: { } node } || item.IsPlaceholder)
        {
            doc.Breadcrumb = item?.Label ?? string.Empty;
            doc.BreadcrumbKind = item?.IconKind;
            doc.Message = "No documentation for this row.";
            return;
        }

        doc.Breadcrumb = string.Join("  ›  ", item.Breadcrumb());
        doc.BreadcrumbKind = item.Kind;
        doc.Provider = server.Provider;
        doc.IsLoading = true;
        doc.Message = "Loading…";
        try
        {
            var properties = await ObjectExplorer.GetPropertiesAsync(server, PasswordFor(server), node.Ref);
            if (version != _selectionVersion)
            {
                return;
            }

            var isGrid = properties.GridColumns.Count > 0;
            doc.FirstHeader = isGrid ? "Name" : "Property";
            doc.SecondHeader = isGrid ? "Details" : "Value";
            var rows = isGrid
                ? properties.GridRows.Select(r => new PropertyRow(r.ElementAtOrDefault(0) ?? string.Empty, r.ElementAtOrDefault(1)))
                : properties.Properties.Select(p => new PropertyRow(p.Name, p.Value));
            foreach (var row in rows)
            {
                doc.Rows.Add(row);
            }

            doc.Sql = properties.Sql;
            doc.Message = doc.Rows.Count == 0 && doc.Sql is null ? (isGrid ? "No objects." : "No properties.") : null;
        }
        catch (IntrospectionException ex)
        {
            if (version == _selectionVersion)
            {
                doc.Message = ObjectExplorer.IsAuthenticationError(ex)
                    ? "Not connected. Expand the data source to sign in."
                    : ex.Message;
            }
        }
        finally
        {
            if (version == _selectionVersion)
            {
                doc.IsLoading = false;
            }
        }
    }

    /// <summary>Opens an object's DDL in a tab (Ctrl+B, "Go to DDL"), reusing the tab when it is open.</summary>
    /// <param name="item">The row.</param>
    /// <returns>The tab, or <c>null</c> when the object has no DDL.</returns>
    public async Task<SqlDocumentViewModel?> OpenDdlAsync(TreeItemViewModel item)
    {
        if (item is not { Server: { } server, Node: { } node } || item.IsPlaceholder)
        {
            return null;
        }

        var key = $"{server.Id}|{node.Ref.Id}";
        if (Tabs.OfType<SqlDocumentViewModel>().FirstOrDefault(t => t.Key == key) is { } open)
        {
            ActiveTab = open;
            return open;
        }

        try
        {
            var properties = await ObjectExplorer.GetPropertiesAsync(server, PasswordFor(server), node.Ref);
            if (properties.Sql is not { } sql)
            {
                Status = $"{item.Label} has no DDL.";
                return null;
            }

            var doc = new SqlDocumentViewModel($"{item.Label} · DDL", item.Kind, key, server.Color)
            {
                Sql = sql,
                Message = null,
                Provider = server.Provider,
                Breadcrumb = string.Join("  ›  ", item.Breadcrumb()),
                BreadcrumbKind = item.Kind,
                OpenInQueryTool = text => _ = OpenQueryAsync(server, node.Ref.Database, text),
            };
            AddTab(doc);
            return doc;
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Opens a row the way DataGrip's double-click and F4 do: tables and views
    /// in the data editor (a column opens its table), other objects as DDL.
    /// </summary>
    /// <param name="item">The row.</param>
    /// <returns><c>true</c> when something opened; <c>false</c> for rows that only expand.</returns>
    public bool OpenItem(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { } r || item.IsFolder)
        {
            return false;
        }

        switch (r.Kind)
        {
            case NodeKind.Table or NodeKind.View or NodeKind.MaterializedView or NodeKind.ForeignTable:
                OpenData(item);
                return true;
            case NodeKind.Column when r.Relation is { } relation:
                OpenData(server, r.Database, new TableRef(relation, r.Schema));
                return true;
            case NodeKind.Server or NodeKind.Database or NodeKind.Schema or NodeKind.Catalog:
                return false;
            default:
                _ = OpenDdlAsync(item);
                return true;
        }
    }

    /// <summary>Gets the qualified name of an object (Copy Reference).</summary>
    /// <param name="item">The row.</param>
    /// <returns>The reference, or the row's label.</returns>
    public static string ReferenceOf(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node?.Ref is not { Name: { } name } r)
        {
            return item.Label;
        }

        var parts = new List<string?>();
        if (r.Kind is NodeKind.Database)
        {
            return SqlKeywords.QuoteIfNeeded(name, server.Provider);
        }

        if (r.Kind is NodeKind.Column && r.Relation is { } relation)
        {
            parts.AddRange([r.Schema, relation, name]);
        }
        else
        {
            parts.AddRange([r.Kind == NodeKind.Schema ? null : r.Schema, name]);
        }

        return string.Join('.', parts.Where(p => p is not null && !(server.Provider == Bengine.Enums.DatabaseProvider.MySql && p == r.Database))
            .Select(p => SqlKeywords.QuoteIfNeeded(p!, server.Provider)));
    }

    private async Task<bool> EnsureConnectedAsync(ServerProfile server)
    {
        try
        {
            await ObjectExplorer.TestAsync(server, PasswordFor(server));
            return true;
        }
        catch (IntrospectionException ex) when (ObjectExplorer.IsAuthenticationError(ex))
        {
            return await SignInAsync(server);
        }
        catch (IntrospectionException ex)
        {
            Status = ex.Message;
            return false;
        }
    }

    private void AddTab(DocumentViewModel doc)
    {
        doc.Close = () => _ = CloseTabAsync(doc);
        Tabs.Add(doc);
        ActiveTab = doc;
    }
}
