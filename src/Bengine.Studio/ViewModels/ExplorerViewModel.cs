namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Bengine.Introspection;
using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Tree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>
/// Drives the Studio window, laid out like pgAdmin: the Object Explorer tree of
/// registered servers (shared with the web explorer through the same
/// <see cref="ServerStore"/>), the Properties and SQL tabs for the selected
/// node, and closable View/Edit Data and Query Tool tabs. Passwords that are not
/// saved are asked for on first connect and kept in memory for the session.
/// </summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    private static readonly HashSet<NodeKind> RelationKinds =
        [NodeKind.Table, NodeKind.View, NodeKind.MaterializedView, NodeKind.ForeignTable];

    private readonly ServerStore _store;
    private readonly Dictionary<string, string> _sessionPasswords = new();
    private int _selectionVersion;

    [ObservableProperty] private TreeItemViewModel? _selectedItem;
    [ObservableProperty] private DocumentViewModel? _activeTab;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _breadcrumb = string.Empty;

    /// <summary>Creates the explorer over a server store.</summary>
    /// <param name="store">The registered-server list.</param>
    public ExplorerViewModel(ServerStore store)
    {
        _store = store;
        PropertiesTab = new PropertiesDocumentViewModel();
        SqlTab = new SqlDocumentViewModel();
        SqlTab.OpenInQueryTool = sql =>
        {
            if (SelectedItem is { Server: { } server } item)
            {
                _ = OpenQueryAsync(server, item.Node?.Ref.Database, sql);
            }
        };
        Tabs.Add(PropertiesTab);
        Tabs.Add(SqlTab);
        ActiveTab = PropertiesTab;
        ReloadServers();
    }

    /// <summary>Gets or sets the dialogs, provided by the window.</summary>
    public IDialogService? Dialogs { get; set; }

    /// <summary>Gets the server groups at the root of the tree.</summary>
    public ObservableCollection<TreeItemViewModel> Roots { get; } = [];

    /// <summary>Gets the workspace tabs: Properties, SQL, then any open Data/Query tabs.</summary>
    public ObservableCollection<DocumentViewModel> Tabs { get; } = [];

    /// <summary>Gets the Properties tab.</summary>
    public PropertiesDocumentViewModel PropertiesTab { get; }

    /// <summary>Gets the SQL tab.</summary>
    public SqlDocumentViewModel SqlTab { get; }

    /// <summary>Gets whether any server is registered.</summary>
    public bool HasServers => Roots.Count > 0;

    /// <summary>Gets the file the server list is stored in.</summary>
    public string StorePath => _store.FilePath;

    /// <summary>Reloads the server list from the store, rebuilding the tree.</summary>
    public void ReloadServers()
    {
        Roots.Clear();
        foreach (var group in _store.List().GroupBy(s => s.Group))
        {
            var row = TreeItemViewModel.ForGroup(this, group.Key, group.Count());
            foreach (var server in group)
            {
                row.Children.Add(TreeItemViewModel.ForServer(this, server, row));
            }

            row.IsExpanded = true;
            Roots.Add(row);
        }

        OnPropertyChanged(nameof(HasServers));
    }

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

    /// <summary>Opens a View/Edit Data tab for a relation node, or focuses the existing one.</summary>
    /// <param name="item">The relation's tree row.</param>
    public void OpenData(TreeItemViewModel item)
    {
        if (item.Server is not { } server || item.Node is not { } node || !RelationKinds.Contains(node.Ref.Kind))
        {
            return;
        }

        var table = new TableRef(node.Ref.Name!, node.Ref.Schema, node.Ref.Kind == NodeKind.Table ? TableKind.Table : TableKind.View);
        var database = node.Ref.Database;
        var existing = Tabs.OfType<DataDocumentViewModel>()
            .FirstOrDefault(d => d.Table == table && d.Location == $"{server.Name} / {database}");
        if (existing is not null)
        {
            ActiveTab = existing;
            return;
        }

        var doc = new DataDocumentViewModel(() => OpenSessionAsync(server, database), Dialogs, server.Provider, table, $"{server.Name} / {database}");
        AddTab(doc);
        _ = doc.InitializeAsync();
    }

    /// <summary>Opens a Query Tool on a database of a server.</summary>
    /// <param name="server">The server.</param>
    /// <param name="database">The database, or <c>null</c> for the maintenance database.</param>
    /// <param name="sql">Initial SQL, if any.</param>
    /// <returns>A task that completes when the tab is open.</returns>
    public async Task OpenQueryAsync(ServerProfile server, string? database, string? sql = null)
    {
        if (!await EnsureConnectedAsync(server))
        {
            return;
        }

        var db = database ?? server.MaintenanceDatabase;
        var title = $"{db ?? server.Name}/{server.Username}@{server.Name}";
        AddTab(new QueryDocumentViewModel(() => OpenSessionAsync(server, db), title, $"{server.Name} › {db ?? "(default)"}", sql));
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
            Status = $"Imported {count} server(s) from pgAdmin.";
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (SelectedItem is { Node: not null } item)
        {
            await item.RefreshAsync();
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
        foreach (var tab in Tabs.Where(t => t is DataDocumentViewModel or QueryDocumentViewModel).ToList())
        {
            Tabs.Remove(tab);
        }

        ActiveTab = PropertiesTab;
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
        item.Reset();
        Status = $"Disconnected from {server.Name}.";
    }

    partial void OnSelectedItemChanged(TreeItemViewModel? value) => _ = LoadPropertiesAsync(value);

    private async Task LoadPropertiesAsync(TreeItemViewModel? item)
    {
        var version = ++_selectionVersion;
        PropertiesTab.Rows.Clear();
        SqlTab.Sql = null;
        if (item is not { Server: { } server, Node: { } node } || item.IsPlaceholder)
        {
            SetBreadcrumb(item?.Label ?? string.Empty, null);
            PropertiesTab.Message = "Select an object in the Object Explorer.";
            SqlTab.Message = "Select an object to see its SQL.";
            return;
        }

        SetBreadcrumb(string.Join("  ›  ", item.Breadcrumb()), item.Kind);
        PropertiesTab.IsLoading = true;
        PropertiesTab.Message = "Loading…";
        try
        {
            var properties = await ObjectExplorer.GetPropertiesAsync(server, PasswordFor(server), node.Ref);
            if (version != _selectionVersion)
            {
                return;
            }

            var isGrid = properties.GridColumns.Count > 0;
            PropertiesTab.FirstHeader = isGrid ? "Name" : "Property";
            PropertiesTab.SecondHeader = isGrid ? "Details" : "Value";
            var rows = isGrid
                ? properties.GridRows.Select(r => new PropertyRow(r.ElementAtOrDefault(0) ?? string.Empty, r.ElementAtOrDefault(1)))
                : properties.Properties.Select(p => new PropertyRow(p.Name, p.Value));
            foreach (var row in rows)
            {
                PropertiesTab.Rows.Add(row);
            }

            PropertiesTab.Message = PropertiesTab.Rows.Count == 0 ? (isGrid ? "No objects." : "No properties.") : null;
            SqlTab.Sql = properties.Sql;
            SqlTab.Message = properties.Sql is null ? "No SQL for this object." : null;
        }
        catch (IntrospectionException ex)
        {
            if (version != _selectionVersion)
            {
                return;
            }

            PropertiesTab.Message = ObjectExplorer.IsAuthenticationError(ex)
                ? "Not connected. Expand the server (or right-click → Connect Server) to sign in."
                : ex.Message;
            SqlTab.Message = PropertiesTab.Message;
        }
        finally
        {
            if (version == _selectionVersion)
            {
                PropertiesTab.IsLoading = false;
            }
        }
    }

    private void SetBreadcrumb(string text, string? kind)
    {
        Breadcrumb = text;
        PropertiesTab.Breadcrumb = SqlTab.Breadcrumb = text;
        PropertiesTab.BreadcrumbKind = SqlTab.BreadcrumbKind = kind;
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

    private Task<DatabaseSession> OpenSessionAsync(ServerProfile server, string? database) =>
        ObjectExplorer.OpenSessionAsync(server, PasswordFor(server), database);

    private void AddTab(DocumentViewModel doc)
    {
        doc.Close = () =>
        {
            var index = Tabs.IndexOf(doc);
            Tabs.Remove(doc);
            if (ActiveTab == doc || ActiveTab is null)
            {
                ActiveTab = Tabs[Math.Clamp(index - 1, 0, Tabs.Count - 1)];
            }
        };
        Tabs.Add(doc);
        ActiveTab = doc;
    }
}
