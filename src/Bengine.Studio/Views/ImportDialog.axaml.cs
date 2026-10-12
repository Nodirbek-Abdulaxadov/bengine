namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Bengine.Introspection;
using Bengine.Introspection.Servers;

/// <summary>Lists the servers registered in the local pgAdmin and imports the new ones; returns the count imported.</summary>
public partial class ImportDialog : Window
{
    private readonly ServerStore _store;
    private readonly List<ServerProfile> _fresh = [];

    /// <summary>Creates the dialog (for the designer).</summary>
    public ImportDialog()
        : this(new ServerStore())
    {
    }

    /// <summary>Creates the dialog over the server store.</summary>
    /// <param name="store">The store to import into.</param>
    public ImportDialog(ServerStore store)
    {
        _store = store;
        AvaloniaXamlLoader.Load(this);
        var info = this.FindControl<TextBlock>("Info")!;
        var button = this.FindControl<Button>("ImportButton")!;
        var path = PgAdminImporter.FindDatabase();
        if (path is null)
        {
            info.Text = "No pgAdmin 4 configuration was found on this machine.";
            button.IsEnabled = false;
            return;
        }

        try
        {
            var existing = store.List();
            var rows = PgAdminImporter.Read(path).Select(s =>
            {
                var exists = existing.Any(e => string.Equals(e.Name, s.Name, StringComparison.OrdinalIgnoreCase) && e.Host == s.Host && e.Port == s.Port);
                if (!exists)
                {
                    _fresh.Add(s);
                }

                return new { s.Name, s.Group, s.Endpoint, s.MaintenanceDatabase, s.Username, State = exists ? "already added" : "new" };
            }).ToList();
            this.FindControl<DataGrid>("Grid")!.ItemsSource = rows;
            info.Text = path;
            button.Content = _fresh.Count > 0 ? $"Import {_fresh.Count} server(s)" : "Nothing new";
            button.IsEnabled = _fresh.Count > 0;
        }
        catch (IntrospectionException ex)
        {
            info.Text = ex.Message;
            button.IsEnabled = false;
        }
    }

    private void OnImport(object? sender, RoutedEventArgs e)
    {
        foreach (var server in _fresh)
        {
            _store.Upsert(server);
        }

        Close(_fresh.Count);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(0);
}
