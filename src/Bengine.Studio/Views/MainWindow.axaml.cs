namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Bengine.Introspection.Servers;
using Bengine.Studio.ViewModels;

/// <summary>
/// The Studio main window. Hosts the Object Explorer and the workspace, builds
/// the tree's context menu, and implements <see cref="IDialogService"/> so view
/// models can ask for passwords, confirmations, and files.
/// </summary>
public partial class MainWindow : Window, IDialogService
{
    private static readonly HashSet<string> RelationKinds = ["Table", "View", "MaterializedView", "ForeignTable"];

    private readonly ServerStore _store;
    private readonly TreeView _tree;

    /// <summary>Creates the window (for the designer).</summary>
    public MainWindow()
        : this(new ServerStore())
    {
    }

    /// <summary>Creates the window over a server store.</summary>
    /// <param name="store">The registered-server list.</param>
    public MainWindow(ServerStore store)
    {
        _store = store;
        AvaloniaXamlLoader.Load(this);
        _tree = this.FindControl<TreeView>("Tree")!;
        _tree.ContextRequested += OnTreeContextRequested;
        _tree.DoubleTapped += OnTreeDoubleTapped;
        _tree.KeyDown += OnTreeKeyDown;
    }

    private ExplorerViewModel? ViewModel => DataContext as ExplorerViewModel;

    /// <inheritdoc />
    public async Task<PasswordAnswer?> AskPasswordAsync(ServerProfile server, string? error) =>
        await new PasswordDialog(server, error).ShowDialog<PasswordAnswer?>(this);

    /// <inheritdoc />
    public async Task<ServerProfile?> EditServerAsync(ServerProfile? server)
    {
        var form = new ServerFormViewModel(_store, server);
        await new ServerDialog { DataContext = form }.ShowDialog(this);
        return form.Saved;
    }

    /// <inheritdoc />
    public async Task<int> ImportFromPgAdminAsync() => await new ImportDialog(_store).ShowDialog<int>(this);

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message) =>
        await new ConfirmDialog(title, message).ShowDialog<bool>(this);

    /// <inheritdoc />
    public async Task<string?> SaveFileAsync(string suggestedName)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = suggestedName,
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    private void OnCloseTab(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DocumentViewModel doc)
        {
            doc.Close?.Invoke();
        }

        e.Handled = true;
    }

    private static TreeItemViewModel? ItemFrom(object? source) =>
        (source as Control)?.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<TreeItemViewModel>().FirstOrDefault();

    private void OnTreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemFrom(e.Source) is { IsPlaceholder: false } item)
        {
            Activate(item);
        }
    }

    private void OnTreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.SelectedItem is not { } item)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            Activate(item);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            ViewModel.RefreshCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Activate(TreeItemViewModel item)
    {
        if (RelationKinds.Contains(item.Kind))
        {
            ViewModel?.OpenData(item);
        }
        else if (item.Children.Count > 0)
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void OnTreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (ViewModel is not { } vm || ItemFrom(e.Source) is not { IsPlaceholder: false } item)
        {
            return;
        }

        vm.SelectedItem = item;
        var menu = new ContextMenu { ItemsSource = BuildMenu(vm, item) };
        menu.Open(_tree);
        e.Handled = true;
    }

    private static List<Control> BuildMenu(ExplorerViewModel vm, TreeItemViewModel item)
    {
        var items = new List<Control>();
        void Add(string header, Action action) => items.Add(Menu(header, action));

        switch (item.Kind)
        {
            case "ServerGroup":
                Add("Register Server…", () => vm.RegisterServerCommand.Execute(null));
                Add("Import Servers (pgAdmin / DataGrip)…", () => vm.ImportPgAdminCommand.Execute(null));
                Add("Refresh", vm.ReloadServers);
                break;
            case "Server":
                if (item.IsConnected)
                {
                    Add("Disconnect Server", () => vm.Disconnect(item));
                }
                else
                {
                    Add("Connect Server", () => item.IsExpanded = true);
                }

                Add("Query Tool", () => _ = vm.OpenQueryAsync(item.Server!, null));
                Add("Refresh", () => _ = item.RefreshAsync());
                items.Add(new Separator());
                Add("Properties…", () => _ = vm.EditServerAsync(item));
                Add("Remove Server", () => _ = vm.RemoveServerAsync(item));
                break;
            case "Database":
                Add("Query Tool", () => _ = vm.OpenQueryAsync(item.Server!, item.Node!.Ref.Database));
                Add("Refresh", () => _ = item.RefreshAsync());
                break;
            case var kind when RelationKinds.Contains(kind):
            {
                var r = item.Node!.Ref;
                Add("View/Edit Data", () => vm.OpenData(item));
                Add("Query Tool", () => _ = vm.OpenQueryAsync(item.Server!, r.Database, SelectTop(item.Server!.Provider, r.Schema, r.Name!)));
                Add("Refresh", () => _ = item.RefreshAsync());
                items.Add(new Separator());
                Add("Copy Name", () => _ = TopLevelClipboard(item)?.SetTextAsync(r.Name ?? item.Label));
                break;
            }

            default:
                Add("Refresh", () => _ = item.RefreshAsync());
                if (item.Node?.Ref.Name is { } name)
                {
                    Add("Copy Name", () => _ = TopLevelClipboard(item)?.SetTextAsync(name));
                }

                break;
        }

        return items;
    }

    // A provider-correct "first 100 rows" query for the Query Tool.
    private static string SelectTop(Bengine.Enums.DatabaseProvider provider, string? schema, string name)
    {
        string Quote(string id) => provider switch
        {
            Bengine.Enums.DatabaseProvider.SqlServer => $"[{id.Replace("]", "]]", StringComparison.Ordinal)}]",
            Bengine.Enums.DatabaseProvider.MySql => $"`{id.Replace("`", "``", StringComparison.Ordinal)}`",
            _ => System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z_][a-z0-9_$]*$") ? id : $"\"{id.Replace("\"", "\"\"", StringComparison.Ordinal)}\"",
        };

        var target = schema is null ? Quote(name) : $"{Quote(schema)}.{Quote(name)}";
        return provider == Bengine.Enums.DatabaseProvider.SqlServer
            ? $"SELECT TOP 100 * FROM {target};"
            : $"SELECT * FROM {target}\nLIMIT 100;";
    }

    private static Avalonia.Input.Platform.IClipboard? TopLevelClipboard(TreeItemViewModel item) =>
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow?.Clipboard;

    private static MenuItem Menu(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
