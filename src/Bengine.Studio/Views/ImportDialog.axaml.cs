namespace Bengine.Studio.Views;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Bengine.Introspection;
using Bengine.Introspection.Servers;

/// <summary>
/// Imports server registrations from pgAdmin 4 or from DataGrip (and the other
/// JetBrains IDEs); returns the number imported. Only new servers are added.
/// </summary>
public partial class ImportDialog : Window
{
    private readonly ServerStore _store;
    private readonly List<ServerProfile> _fresh = [];
    private bool _ready;

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
        this.FindControl<TabStrip>("Source")!.SelectedIndex = 0;
        _ready = true;
        Scan();
    }

    private bool IsDataGrip => this.FindControl<TabStrip>("Source")!.SelectedIndex == 1;

    private void OnSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The tab strip selects its first item while the XAML is still loading,
        // before named controls can be found.
        if (!_ready)
        {
            return;
        }

        if (this.FindControl<DockPanel>("PathRow") is { } row)
        {
            row.IsVisible = IsDataGrip;
            Scan();
        }
    }

    private void OnScan(object? sender, RoutedEventArgs e) => Scan();

    private void Scan()
    {
        var info = this.FindControl<TextBlock>("Info")!;
        var skippedText = this.FindControl<TextBlock>("Skipped")!;
        var button = this.FindControl<Button>("ImportButton")!;
        var grid = this.FindControl<DataGrid>("Grid")!;
        _fresh.Clear();
        skippedText.Text = null;
        this.FindControl<TextBlock>("Hint")!.Text = IsDataGrip
            ? "Passwords are not imported — DataGrip keeps them in the OS keyring. You will be asked on first connect."
            : "Passwords are not imported — pgAdmin encrypts them. You will be asked on first connect.";

        var existing = _store.List();
        bool Exists(ServerProfile s) => existing.Any(e => string.Equals(e.Name, s.Name, StringComparison.OrdinalIgnoreCase) && e.Host == s.Host && e.Port == s.Port);

        IEnumerable<ServerProfile> found;
        try
        {
            if (IsDataGrip)
            {
                var result = DataGripImporter.Read(DataGripImporter.FindConfigFiles(this.FindControl<TextBox>("PathBox")!.Text));
                info.Text = result.Files.Count == 0
                    ? "No DataGrip / JetBrains data sources were found. Give a project folder above to scan it."
                    : string.Join("  ·  ", result.Files);
                skippedText.Text = result.Skipped.Count == 0 ? null : "Skipped: " + string.Join(", ", result.Skipped.Select(s => $"{s.Name} ({s.Reason})"));
                found = result.Servers.Select(c => c.Server);
            }
            else
            {
                var path = PgAdminImporter.FindDatabase();
                info.Text = path ?? "No pgAdmin 4 configuration was found on this machine.";
                found = path is null ? [] : PgAdminImporter.Read(path);
            }
        }
        catch (IntrospectionException ex)
        {
            info.Text = ex.Message;
            found = [];
        }

        grid.ItemsSource = found.Select(s =>
        {
            var exists = Exists(s);
            if (!exists)
            {
                _fresh.Add(s);
            }

            return new { s.Name, s.Group, Provider = s.Provider.ToString(), s.Endpoint, Database = s.MaintenanceDatabase, User = s.Username, State = exists ? "already added" : "new" };
        }).ToList();
        button.Content = _fresh.Count > 0 ? $"Import {_fresh.Count} server(s)" : "Nothing new";
        button.IsEnabled = _fresh.Count > 0;
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
