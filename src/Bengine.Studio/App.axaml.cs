namespace Bengine.Studio;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Bengine.Introspection.Servers;
using Bengine.Studio.ViewModels;
using Bengine.Studio.Views;

/// <summary>The Avalonia application: loads XAML and shows the explorer window.</summary>
public sealed class App : Application
{
    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The same server list the web explorer uses.
            var store = new ServerStore();
            var window = new MainWindow(store);
            var explorer = new ExplorerViewModel(store) { Dialogs = window };
            window.DataContext = explorer;

            // Tabs, tree, and window come back as they were left.
            window.RestoreWorkspace(Path.Combine(Path.GetDirectoryName(store.FilePath) ?? ".", "workspace.json"));
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
