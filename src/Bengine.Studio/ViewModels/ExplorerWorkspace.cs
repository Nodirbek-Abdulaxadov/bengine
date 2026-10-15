namespace Bengine.Studio.ViewModels;

using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;

/// <summary>
/// Saving and restoring the workspace across restarts (<see cref="WorkspaceState"/>).
/// Restored tabs connect only when they are first shown, and a server that
/// was open reconnects at once only when no password has to be typed; the
/// rows below it re-expand as they load, so a server signed into later
/// still comes back as it was.
/// </summary>
public sealed partial class ExplorerViewModel
{
    private readonly HashSet<string> _pendingExpanded = new(StringComparer.Ordinal);
    private readonly Dictionary<DocumentViewModel, Func<Task>> _deferred = [];
    private string? _pendingSelected;

    /// <summary>Captures the tree and the tabs (the window adds its own placement).</summary>
    /// <returns>The state.</returns>
    public WorkspaceState CaptureWorkspace()
    {
        var state = new WorkspaceState();
        void Walk(TreeItemViewModel item)
        {
            if (item.IsPlaceholder)
            {
                return;
            }

            if (item.Server is not null && item.IsExpanded && item.IsLoaded)
            {
                state.Expanded.Add(item.StateKey);
            }

            foreach (var child in item.Children)
            {
                Walk(child);
            }
        }

        foreach (var root in Roots)
        {
            Walk(root);
        }

        // A server not connected this session keeps the rows it had open.
        var connected = ServerItems.Where(s => s.IsLoaded).Select(s => s.Server!.Id).ToHashSet(StringComparer.Ordinal);
        state.Expanded.AddRange(_pendingExpanded.Where(k => !connected.Contains(TreeItemViewModel.ServerIdOf(k)) && !state.Expanded.Contains(k)).Order(StringComparer.Ordinal));
        state.Selected = SelectedItem is { IsPlaceholder: false, Server: not null } selected ? selected.StateKey : _pendingSelected;

        foreach (var tab in Tabs)
        {
            if (TabStateOf(tab) is { } saved)
            {
                if (tab == ActiveTab)
                {
                    state.ActiveTab = state.Tabs.Count;
                }

                state.Tabs.Add(saved);
            }
        }

        return state;
    }

    /// <summary>Reopens the tabs and re-expands the tree of a saved workspace.</summary>
    /// <param name="state">The state.</param>
    public void RestoreWorkspace(WorkspaceState state)
    {
        _pendingExpanded.UnionWith(state.Expanded);
        _pendingSelected = state.Selected;

        DocumentViewModel? active = null;
        for (var i = 0; i < state.Tabs.Count; i++)
        {
            if (RestoreTab(state.Tabs[i]) is not { } doc)
            {
                continue;
            }

            doc.Close = () => _ = CloseTabAsync(doc);
            Tabs.Add(doc);
            if (i == state.ActiveTab)
            {
                active = doc;
            }
        }

        foreach (var item in ServerItems.ToList())
        {
            if (item.StateKey == _pendingSelected)
            {
                SelectedItem = item;
                _pendingSelected = null;
            }

            if (item.Server is { } server && _pendingExpanded.Contains(item.StateKey) && ConnectsQuietly(server))
            {
                _pendingExpanded.Remove(item.StateKey);
                item.IsExpanded = true;
            }
        }

        // Showing the active tab starts it; the others start when first shown.
        ActiveTab = active ?? Tabs.LastOrDefault();
    }

    /// <summary>Re-expands and reselects the freshly loaded children of a row, as they were saved.</summary>
    /// <param name="parent">The row whose children just loaded.</param>
    internal void RestoreChildren(TreeItemViewModel parent)
    {
        if (_pendingExpanded.Count == 0 && _pendingSelected is null)
        {
            return;
        }

        foreach (var child in parent.Children.ToList())
        {
            if (child.IsPlaceholder)
            {
                continue;
            }

            var key = child.StateKey;
            if (key == _pendingSelected)
            {
                SelectedItem = child;
                _pendingSelected = null;
            }

            if (_pendingExpanded.Remove(key))
            {
                child.IsExpanded = true;
            }
        }
    }

    // Opening a tab restored from the workspace starts it.
    partial void OnActiveTabChanged(DocumentViewModel? value)
    {
        if (value is not null && _deferred.Remove(value, out var start))
        {
            _ = start();
        }
    }

    // SQLite and servers with a saved (or no) password connect without asking.
    private bool ConnectsQuietly(ServerProfile server) =>
        server.Provider == Bengine.Enums.DatabaseProvider.Sqlite
        || !string.IsNullOrEmpty(PasswordFor(server))
        || string.IsNullOrEmpty(server.Username);

    private static TabState? TabStateOf(DocumentViewModel tab) => tab switch
    {
        QueryDocumentViewModel query => new TabState
        {
            Kind = "query",
            ServerId = query.Server.Id,
            Database = query.Database,
            Text = query.FilePath is null || query.IsDirty ? query.QueryText : null,
            FilePath = query.FilePath,
            Unsaved = query.IsDirty,
            MaxRows = query.MaxRows,
        },
        DataDocumentViewModel data => new TabState
        {
            Kind = "data",
            ServerId = data.Server.Id,
            Database = data.Database,
            Table = data.Table.Name,
            Schema = data.Table.Schema,
            IsView = data.Table.Kind == TableKind.View,
            Where = string.IsNullOrWhiteSpace(data.WhereText) ? null : data.WhereText,
            OrderBy = string.IsNullOrWhiteSpace(data.OrderByText) ? null : data.OrderByText,
            PageSize = data.PageSize,
        },
        SqlDocumentViewModel { Key: not null, ServerId: not null } ddl => new TabState
        {
            Kind = "ddl",
            ServerId = ddl.ServerId,
            Database = ddl.Database,
            Text = ddl.Sql,
            Title = ddl.Title,
            IconKind = ddl.IconKind,
            Key = ddl.Key,
            Breadcrumb = ddl.Breadcrumb,
        },
        ActivityDocumentViewModel activity => new TabState { Kind = "activity", ServerId = activity.Server.Id },
        DiagramDocumentViewModel diagram => new TabState { Kind = "diagram", ServerId = diagram.Server.Id, Database = diagram.Database, Schema = diagram.Schema },
        _ => null,
    };

    private DocumentViewModel? RestoreTab(TabState tab)
    {
        if (tab.ServerId is null || _store.Get(tab.ServerId) is not { } server)
        {
            return null;
        }

        switch (tab.Kind)
        {
            case "query":
                string? file = null;
                var text = tab.Text;
                if (tab.FilePath is { } path && File.Exists(path))
                {
                    try
                    {
                        var saved = File.ReadAllText(path);
                        file = path;
                        text = tab.Unsaved && tab.Text is not null ? tab.Text : saved;
                        var fileQuery = CreateQuery(server, tab.Database, saved, file);
                        if (text != saved)
                        {
                            // Edits made after the last save come back unsaved.
                            fileQuery.QueryText = text;
                        }

                        return Defer(Limited(fileQuery, tab.MaxRows), fileQuery.InitializeAsync);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        file = null;
                    }
                }

                if (text is null)
                {
                    // A saved file that is gone, with nothing unsaved to show.
                    return null;
                }

                var query = CreateQuery(server, tab.Database, text, file);
                return Defer(Limited(query, tab.MaxRows), query.InitializeAsync);
            case "data" when tab.Table is { } table:
                var data = CreateData(server, tab.Database, new TableRef(table, tab.Schema, tab.IsView ? TableKind.View : TableKind.Table), tab.Where, tab.OrderBy, tab.PageSize);
                return Defer(data, data.InitializeAsync);
            case "ddl":
                return new SqlDocumentViewModel(tab.Title ?? "DDL", tab.IconKind, tab.Key, server.Color)
                {
                    Sql = tab.Text,
                    Provider = server.Provider,
                    Breadcrumb = tab.Breadcrumb ?? string.Empty,
                    BreadcrumbKind = tab.IconKind,
                    ServerId = server.Id,
                    Database = tab.Database,
                    OpenInQueryTool = sql => _ = OpenQueryAsync(server, tab.Database, sql),
                };
            case "activity":
                var activity = new ActivityDocumentViewModel(this, server);
                return Defer(activity, activity.InitializeAsync);
            case "diagram":
                var diagram = new DiagramDocumentViewModel(this, server, tab.Database, tab.Schema);
                return Defer(diagram, diagram.InitializeAsync);
            default:
                return null;
        }
    }

    private static QueryDocumentViewModel Limited(QueryDocumentViewModel query, int? maxRows)
    {
        if (maxRows is { } rows && query.Limits.Contains(rows))
        {
            query.MaxRows = rows;
        }

        return query;
    }

    private DocumentViewModel Defer(DocumentViewModel doc, Func<Task> start)
    {
        _deferred[doc] = start;
        return doc;
    }
}
