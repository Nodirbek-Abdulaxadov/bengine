namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>A file or folder of the Files panel.</summary>
public sealed partial class FileItem : ObservableObject
{
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Creates the item.</summary>
    /// <param name="path">The path.</param>
    /// <param name="isDirectory">Whether it is a folder.</param>
    public FileItem(string path, bool isDirectory)
    {
        Path = path;
        IsDirectory = isDirectory;
    }

    /// <summary>Gets the path.</summary>
    public string Path { get; }

    /// <summary>Gets the display name.</summary>
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : Path;

    /// <summary>Gets whether it is a folder.</summary>
    public bool IsDirectory { get; }

    /// <summary>Gets the icon: the file type's, or a folder (open when expanded).</summary>
    public Avalonia.Media.IImage? Icon => Bengine.Studio.Controls.MaterialIcons.ForFile(Name, IsDirectory, IsExpanded);

    /// <summary>Gets the children of a folder.</summary>
    public ObservableCollection<FileItem> Children { get; } = [];

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Icon));
}

/// <summary>
/// The Files panel: the SQL files of the project folder (and the scratch
/// files), to open in Query Tools bound to the file — saved with Ctrl+S,
/// attached to the data source they last ran on, with a local history.
/// </summary>
public sealed partial class FilesViewModel : ObservableObject
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".sql", ".ddl", ".psql", ".mysql", ".tsql" };
    private readonly ExplorerViewModel _explorer;

    [ObservableProperty] private string _folder;
    [ObservableProperty] private FileItem? _selected;

    /// <summary>Creates the panel model.</summary>
    /// <param name="explorer">The explorer.</param>
    /// <param name="folder">The project folder.</param>
    public FilesViewModel(ExplorerViewModel explorer, string folder)
    {
        _explorer = explorer;
        _folder = folder;
        Refresh();
    }

    /// <summary>Gets the top-level items: the project folder and the scratches.</summary>
    public ObservableCollection<FileItem> Roots { get; } = [];

    /// <summary>Reloads the tree.</summary>
    [RelayCommand]
    public void Refresh()
    {
        Roots.Clear();
        var project = Directory.Exists(Folder) ? Load(Folder, depth: 0) : new FileItem(Folder, isDirectory: true);
        project.IsExpanded = true;
        Roots.Add(project);
        if (Directory.Exists(_explorer.Files.ScratchDirectory))
        {
            var scratches = Load(_explorer.Files.ScratchDirectory, depth: 0);
            Roots.Add(scratches);
        }
    }

    /// <summary>Opens a file in a Query Tool.</summary>
    /// <param name="item">The file.</param>
    /// <returns>A task that completes when the tab is open.</returns>
    public Task OpenAsync(FileItem item) => item.IsDirectory ? Task.CompletedTask : _explorer.OpenFileAsync(item.Path);

    /// <summary>Shows a file's local history; a restored version is written back (or into its open tab).</summary>
    /// <param name="item">The file.</param>
    /// <returns>A task that completes when the dialog closes.</returns>
    public async Task ShowHistoryAsync(FileItem item)
    {
        if (item.IsDirectory || _explorer.Dialogs is not { } dialogs)
        {
            return;
        }

        if (_explorer.Tabs.OfType<QueryDocumentViewModel>().FirstOrDefault(q => q.FilePath == Path.GetFullPath(item.Path)) is { } open)
        {
            _explorer.ActiveTab = open;
            await open.ShowLocalHistoryAsync();
            return;
        }

        var current = File.Exists(item.Path) ? await File.ReadAllTextAsync(item.Path) : string.Empty;
        if (await dialogs.ShowLocalHistoryAsync($"Local History — {item.Name}", _explorer.Files.History(item.Path), current) is { } restored)
        {
            _explorer.Files.Save(item.Path, restored);
            _explorer.Status = $"Restored {item.Name} from local history.";
        }
    }

    /// <summary>Renames a file (and its open tab).</summary>
    /// <param name="item">The file.</param>
    /// <returns>A task that completes when renamed.</returns>
    public async Task RenameAsync(FileItem item)
    {
        if (item.IsDirectory || _explorer.Dialogs is not { } dialogs
            || await dialogs.PromptAsync("Rename File", "New name:", item.Name) is not { Length: > 0 } name || name == item.Name)
        {
            return;
        }

        var target = Path.Combine(Path.GetDirectoryName(item.Path)!, name);
        if (File.Exists(target))
        {
            _explorer.Status = $"{name} already exists.";
            return;
        }

        File.Move(item.Path, target);
        if (_explorer.Files.TargetOf(item.Path) is { } attached)
        {
            _explorer.Files.Attach(target, attached);
        }

        foreach (var tab in _explorer.Tabs.OfType<QueryDocumentViewModel>().Where(q => q.FilePath == Path.GetFullPath(item.Path)))
        {
            tab.FilePath = Path.GetFullPath(target);
        }

        Refresh();
    }

    /// <summary>Deletes a file after confirmation (its local history stays).</summary>
    /// <param name="item">The file.</param>
    /// <returns>A task that completes when deleted.</returns>
    public async Task DeleteAsync(FileItem item)
    {
        if (item.IsDirectory || _explorer.Dialogs is not { } dialogs
            || !await dialogs.ConfirmAsync("Delete File", $"Delete {item.Name}? Its versions stay in local history."))
        {
            return;
        }

        File.Delete(item.Path);
        Refresh();
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (_explorer.Dialogs is { } dialogs && await dialogs.OpenFileAsync("Open SQL File", ["sql", "ddl"]) is { } path)
        {
            await _explorer.OpenFileAsync(path);
        }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        if (_explorer.Dialogs is { } dialogs && await dialogs.PickFolderAsync("Project folder") is { } folder)
        {
            Folder = folder;
            _explorer.Settings.ProjectFolder = folder;
            _explorer.Settings.Save();
            Refresh();
        }
    }

    [RelayCommand]
    private async Task NewFileAsync()
    {
        if (_explorer.Dialogs is not { } dialogs || await dialogs.PromptAsync("New SQL File", "File name:", "query.sql") is not { Length: > 0 } name)
        {
            return;
        }

        var folder = Selected is { IsDirectory: true } dir ? dir.Path : Selected is { } file ? Path.GetDirectoryName(file.Path)! : Folder;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? name : name + ".sql");
        if (!File.Exists(path))
        {
            await File.WriteAllTextAsync(path, string.Empty);
        }

        Refresh();
        await _explorer.OpenFileAsync(path);
    }

    [RelayCommand]
    private async Task NewScratchAsync()
    {
        var path = _explorer.Files.NewScratch();
        Refresh();
        await _explorer.OpenFileAsync(path);
    }

    private static FileItem Load(string folder, int depth)
    {
        var item = new FileItem(folder, isDirectory: true);
        if (depth > 6)
        {
            return item;
        }

        try
        {
            foreach (var directory in Directory.GetDirectories(folder).Where(d => !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase))
            {
                item.Children.Add(Load(directory, depth + 1));
            }

            foreach (var file in Directory.GetFiles(folder).Where(f => Extensions.Contains(Path.GetExtension(f))).Order(StringComparer.OrdinalIgnoreCase))
            {
                item.Children.Add(new FileItem(file, isDirectory: false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable folder shows empty.
        }

        return item;
    }
}
