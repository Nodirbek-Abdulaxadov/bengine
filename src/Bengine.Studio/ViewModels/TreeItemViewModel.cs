namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Controls;
using Bengine.Introspection;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Tree;
using Bengine.Studio.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// One row of the Object Explorer tree: a server group, a registered server, or
/// a catalog node beneath it. Children load lazily the first time the row is
/// expanded; until then a "Loading…" placeholder keeps the expander visible.
/// </summary>
public sealed partial class TreeItemViewModel : ObservableObject
{
    private readonly ExplorerViewModel _owner;
    private bool _loaded;
    private Control? _icon;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isVisible = true;

    private TreeItemViewModel(ExplorerViewModel owner, TreeItemViewModel? parent, string label)
    {
        _owner = owner;
        Parent = parent;
        Label = label;
    }

    /// <summary>Gets the owning server, or <c>null</c> for a group or placeholder row.</summary>
    public ServerProfile? Server { get; private init; }

    /// <summary>Gets the catalog node, or <c>null</c> for a group or placeholder row.</summary>
    public TreeNode? Node { get; private init; }

    /// <summary>Gets the server group name for a group row.</summary>
    public string? Group { get; private init; }

    /// <summary>Gets the parent row.</summary>
    public TreeItemViewModel? Parent { get; }

    /// <summary>Gets the display label.</summary>
    public string Label { get; }

    /// <summary>Gets the dimmed secondary text.</summary>
    public string? Detail { get; private init; }

    /// <summary>Gets the folder child count as "(n)", or <c>null</c>.</summary>
    public string? CountText { get; private set; }

    /// <summary>Gets a value indicating whether this is a transient placeholder or message row.</summary>
    public bool IsPlaceholder { get; private init; }

    /// <summary>Gets a value indicating whether the placeholder row reports an error.</summary>
    public bool IsError { get; private init; }

    /// <summary>Gets the node kind name used for icons and menus.</summary>
    public string Kind => Group is not null ? "ServerGroup" : Node?.Ref.Kind.ToString() ?? "Placeholder";

    /// <summary>Gets the child rows.</summary>
    public ObservableCollection<TreeItemViewModel> Children { get; } = [];

    /// <summary>Gets the server's marking color (<c>#rrggbb</c>), shown as a tint on the server row.</summary>
    public string? AccentColor => Kind == "Server" ? Server?.Color : null;

    /// <summary>Gets a value indicating whether the row belongs to a read-only server.</summary>
    public bool IsReadOnlyServer => Kind == "Server" && Server?.ReadOnly == true;

    /// <summary>Gets the row's icon (rebuilt when the connection state changes).</summary>
    public Control? Icon => IsPlaceholder
        ? null
        : _icon ??= TreeIcons.Create(Kind, Server?.Provider ?? Enums.DatabaseProvider.PostgreSql, IconConnected, Node?.Badge);

    private bool IconConnected => Kind switch
    {
        "Server" => IsConnected,
        "Database" => IsConnected,
        _ => true,
    };

    /// <summary>Creates a server-group row.</summary>
    /// <param name="owner">The explorer.</param>
    /// <param name="name">The group name.</param>
    /// <param name="count">The number of servers in the group.</param>
    /// <returns>The row.</returns>
    public static TreeItemViewModel ForGroup(ExplorerViewModel owner, string name, int count) =>
        new(owner, null, name) { Group = name, CountText = $"({count})" };

    /// <summary>Creates a registered-server row.</summary>
    /// <param name="owner">The explorer.</param>
    /// <param name="server">The server.</param>
    /// <param name="parent">The group row.</param>
    /// <returns>The row.</returns>
    public static TreeItemViewModel ForServer(ExplorerViewModel owner, ServerProfile server, TreeItemViewModel parent)
    {
        var item = new TreeItemViewModel(owner, parent, server.Name)
        {
            Server = server,
            Node = new TreeNode { Ref = new NodeRef { Kind = NodeKind.Server }, Label = server.Name, HasChildren = true },
            Detail = null,
        };
        item.Children.Add(Placeholder(owner, item, "Loading…"));
        return item;
    }

    private static TreeItemViewModel ForNode(ExplorerViewModel owner, ServerProfile server, TreeNode node, TreeItemViewModel parent)
    {
        var item = new TreeItemViewModel(owner, parent, node.Label)
        {
            Server = server,
            Node = node,
            Detail = node.Detail,
            CountText = node.Count is { } count ? $"({count})" : null,
        };
        if (node.HasChildren)
        {
            item.Children.Add(Placeholder(owner, item, "Loading…"));
        }

        return item;
    }

    private static TreeItemViewModel Placeholder(ExplorerViewModel owner, TreeItemViewModel parent, string text, bool error = false) =>
        new(owner, parent, text) { IsPlaceholder = true, IsError = error };

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded && Node is not null && !IsLoading)
        {
            _ = LoadAsync();
        }
    }

    partial void OnIsConnectedChanged(bool value)
    {
        _icon = null;
        OnPropertyChanged(nameof(Icon));
    }

    /// <summary>Loads (or reloads) the row's children from the catalog.</summary>
    /// <returns>A task that completes when the children are shown.</returns>
    public async Task LoadAsync()
    {
        if (Server is null || Node is null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var nodes = await _owner.FetchChildrenAsync(Server, Node.Ref);
            if (nodes is null)
            {
                // The user cancelled the password prompt.
                IsExpanded = false;
                return;
            }

            Children.Clear();
            foreach (var node in nodes)
            {
                Children.Add(ForNode(_owner, Server, node, this));
            }

            _loaded = true;
            if (Kind is "Server" or "Database")
            {
                IsConnected = true;
            }

            if (Kind == "Server")
            {
                _owner.MarkConnected(Server);
            }
        }
        catch (IntrospectionException ex)
        {
            Children.Clear();
            Children.Add(Placeholder(_owner, this, ex.Message, error: true));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Loads the children if needed and expands the row.</summary>
    /// <returns><c>false</c> if loading failed or the sign-in was cancelled.</returns>
    public async Task<bool> EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            await LoadAsync();
        }

        if (_loaded)
        {
            IsExpanded = true;
        }

        return _loaded;
    }

    /// <summary>
    /// Shows only rows whose label contains the text (case-insensitive), with
    /// their ancestors; matching rows' loaded subtrees stay visible. An empty
    /// text shows everything. Only loaded rows are searched.
    /// </summary>
    /// <param name="text">The filter text.</param>
    /// <returns>Whether this row stays visible.</returns>
    public bool ApplyFilter(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            foreach (var child in Children)
            {
                child.ApplyFilter(text);
            }

            IsVisible = true;
            return true;
        }

        // An unloaded subtree ("Loading…") cannot match.
        if (IsPlaceholder)
        {
            return false;
        }

        var self = Label.Contains(text, StringComparison.OrdinalIgnoreCase);
        var anyChild = false;
        foreach (var child in Children)
        {
            anyChild |= self ? child.ApplyFilter(string.Empty) : child.ApplyFilter(text);
        }

        IsVisible = self || anyChild;
        if (anyChild && !self && _loaded)
        {
            IsExpanded = true;
        }

        return IsVisible;
    }

    /// <summary>Drops the loaded children and reloads them if the row is expanded.</summary>
    /// <returns>A task that completes when the refresh is done.</returns>
    public async Task RefreshAsync()
    {
        _loaded = false;
        Children.Clear();
        if (Node?.HasChildren == true)
        {
            Children.Add(Placeholder(_owner, this, "Loading…"));
        }

        if (IsExpanded)
        {
            await LoadAsync();
        }
    }

    /// <summary>Collapses a server row and forgets its children (used on disconnect).</summary>
    public void Reset()
    {
        _loaded = false;
        IsExpanded = false;
        IsConnected = false;
        Children.Clear();
        Children.Add(Placeholder(_owner, this, "Loading…"));
    }

    /// <summary>Gets the breadcrumb path (server › database › schema › object).</summary>
    /// <returns>The path segments.</returns>
    public IReadOnlyList<string> Breadcrumb()
    {
        if (Server is null || Node is null)
        {
            return [Label];
        }

        var parts = new List<string> { Server.Name };
        var r = Node.Ref;
        if (r.Database is { } db && r.Kind != NodeKind.Database)
        {
            parts.Add(db);
        }

        if (r.Schema is { } schema && r.Kind is not (NodeKind.Schema or NodeKind.Catalog) && schema != r.Database)
        {
            parts.Add(schema);
        }

        if (r.Relation is { } relation && relation != r.Name
            && r.Kind is not (NodeKind.Table or NodeKind.View or NodeKind.MaterializedView or NodeKind.ForeignTable))
        {
            parts.Add(relation);
        }

        if (r.Kind != NodeKind.Server)
        {
            parts.Add(Label);
        }

        return parts;
    }
}
