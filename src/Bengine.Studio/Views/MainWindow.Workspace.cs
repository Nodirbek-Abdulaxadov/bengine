namespace Bengine.Studio.Views;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Bengine.Studio.ViewModels;

/// <summary>
/// Keeps the workspace across restarts: the window's placement and side
/// panel, and (through the explorer) the tree and the tabs. The state is
/// written when the window closes and every 20 seconds while it is open, so a
/// crash loses little.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromSeconds(20) };
    private string? _workspacePath;
    private string _savedWorkspace = string.Empty;
    private WorkspaceState? _pendingWorkspace;
    private WindowPlacement? _normalPlacement;

    /// <summary>
    /// Restores the workspace saved in a file: the placement at once (call it
    /// before the window shows), the tree and tabs once the window is open,
    /// so a password prompt has a window to belong to. From then on the
    /// workspace is saved to the same file.
    /// </summary>
    /// <param name="path">The workspace file.</param>
    public void RestoreWorkspace(string path)
    {
        _workspacePath = path;
        var state = WorkspaceState.Load(path);
        ApplyPlacement(state);
        if (IsVisible)
        {
            ViewModel?.RestoreWorkspace(state);
        }
        else
        {
            _pendingWorkspace = state;
        }

        _autosave.Tick -= OnAutosave;
        _autosave.Tick += OnAutosave;
        _autosave.Start();
    }

    /// <summary>Writes the workspace to its file when it changed since the last write.</summary>
    /// <returns>The state written (or that was already on disk), or <c>null</c> when the window keeps no workspace.</returns>
    public WorkspaceState? SaveWorkspace()
    {
        if (_workspacePath is null || ViewModel is not { } vm)
        {
            return null;
        }

        var state = vm.CaptureWorkspace();
        state.Window = _normalPlacement is { } normal ? normal with { Maximized = WindowState == WindowState.Maximized } : null;
        state.SidebarWidth = this.FindControl<Grid>("Body") is { } body ? Math.Round(body.ColumnDefinitions[0].ActualWidth) : null;
        state.SidePanel = this.FindControl<TabControl>("SidePanels")?.SelectedIndex ?? 0;
        var json = state.ToJson();
        if (json == _savedWorkspace)
        {
            return state;
        }

        try
        {
            // Consoles may hold sensitive SQL: the file is the owner's only, like the server list.
            Directory.CreateDirectory(Path.GetDirectoryName(_workspacePath)!);
            var temporary = _workspacePath + ".tmp";
            File.WriteAllText(temporary, json);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporary, _workspacePath, overwrite: true);
            _savedWorkspace = json;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The workspace is a convenience; the next save tries again.
        }

        return state;
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        RememberPlacement();
        if (_pendingWorkspace is { } state && ViewModel is { } vm)
        {
            _pendingWorkspace = null;
            vm.RestoreWorkspace(state);
        }
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _autosave.Stop();
        SaveWorkspace();
        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty || change.Property == WindowStateProperty)
        {
            RememberPlacement();
        }
    }

    private void OnAutosave(object? sender, EventArgs e) => SaveWorkspace();

    // The size and place of the normal (not maximized) window, kept while it changes.
    private void RememberPlacement()
    {
        if (WindowState == WindowState.Normal && IsVisible)
        {
            _normalPlacement = new WindowPlacement(Position.X, Position.Y, ClientSize.Width, ClientSize.Height, false);
        }
    }

    private void ApplyPlacement(WorkspaceState state)
    {
        if (state.Window is { } window && window.Width >= MinWidth && window.Height >= MinHeight)
        {
            Width = window.Width;
            Height = window.Height;
            _normalPlacement = window with { Maximized = false };
            var bounds = new PixelRect(window.X, window.Y, (int)window.Width, (int)window.Height);

            // A position on a screen that is no longer attached is ignored.
            if (Screens.All.Any(s => s.WorkingArea.Intersects(bounds)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Position = new PixelPoint(window.X, window.Y);
            }

            if (window.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }

        if (state.SidebarWidth is { } width and >= 180 and <= 1200 && this.FindControl<Grid>("Body") is { } body)
        {
            body.ColumnDefinitions[0].Width = new GridLength(width);
        }

        if (this.FindControl<TabControl>("SidePanels") is { } panels && state.SidePanel is 0 or 1)
        {
            panels.SelectedIndex = state.SidePanel;
        }
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => RememberPlacement();
}
