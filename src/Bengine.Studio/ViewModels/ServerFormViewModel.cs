namespace Bengine.Studio.ViewModels;

using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Tree;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>A provider choice in the Register Server dialog.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Label">The display name.</param>
public sealed record ProviderOption(DatabaseProvider Provider, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// The Register/Edit Server form (General / Connection / Advanced), with
/// connection testing. Saving writes the registration to the shared
/// <see cref="ServerStore"/>.
/// </summary>
public sealed partial class ServerFormViewModel : ObservableObject
{
    private readonly ServerStore _store;
    private readonly ServerProfile? _existing;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _group = "Servers";
    [ObservableProperty] private string _comment = string.Empty;
    [ObservableProperty] private ProviderOption _selectedProvider;
    [ObservableProperty] private string _host = "localhost";
    [ObservableProperty] private decimal? _port = 5432;
    [ObservableProperty] private string _maintenanceDatabase = "postgres";
    [ObservableProperty] private string _username = "postgres";
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _savePassword = true;
    [ObservableProperty] private string _filePath = string.Empty;
    [ObservableProperty] private string _options = string.Empty;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _tabIndex;

    /// <summary>Creates the form, pre-filled from an existing registration when editing.</summary>
    /// <param name="store">The server store.</param>
    /// <param name="existing">The registration to edit, or <c>null</c> to register a new server.</param>
    public ServerFormViewModel(ServerStore store, ServerProfile? existing)
    {
        _store = store;
        _existing = existing;
        _selectedProvider = ProviderOptions[0];
        if (existing is null)
        {
            return;
        }

        _name = existing.Name;
        _group = existing.Group;
        _comment = existing.Comment ?? string.Empty;
        _selectedProvider = ProviderOptions.First(p => p.Provider == existing.Provider);
        _host = existing.Host ?? string.Empty;
        _port = existing.Port;
        _maintenanceDatabase = existing.MaintenanceDatabase ?? string.Empty;
        _username = existing.Username ?? string.Empty;
        _savePassword = existing.SavePassword;
        _filePath = existing.FilePath ?? string.Empty;
        _options = existing.Options ?? string.Empty;
        _tabIndex = 1;
    }

    /// <summary>Gets the provider choices.</summary>
    public IReadOnlyList<ProviderOption> ProviderOptions { get; } =
    [
        new(DatabaseProvider.PostgreSql, "PostgreSQL"),
        new(DatabaseProvider.MySql, "MySQL / MariaDB"),
        new(DatabaseProvider.SqlServer, "Microsoft SQL Server"),
        new(DatabaseProvider.Sqlite, "SQLite"),
    ];

    /// <summary>Gets the dialog title.</summary>
    public string Title => _existing is null ? "Register — Server" : $"{_existing.Name} — Properties";

    /// <summary>Gets the password watermark (hints that a saved password is kept).</summary>
    public string PasswordHint => _existing?.Password is { Length: > 0 } ? "(saved — leave blank to keep)" : string.Empty;

    /// <summary>Gets whether the provider is SQLite (file-based).</summary>
    public bool IsSqlite => SelectedProvider.Provider == DatabaseProvider.Sqlite;

    /// <summary>Gets whether the provider is a network server.</summary>
    public bool IsNetwork => !IsSqlite;

    /// <summary>Gets the saved registration once <see cref="Save"/> succeeds.</summary>
    public ServerProfile? Saved { get; private set; }

    /// <summary>Raised when the form saved successfully and the dialog should close.</summary>
    public event Action? Completed;

    partial void OnSelectedProviderChanged(ProviderOption value)
    {
        OnPropertyChanged(nameof(IsSqlite));
        OnPropertyChanged(nameof(IsNetwork));
        if (_existing is not null)
        {
            return;
        }

        (Port, MaintenanceDatabase, Username) = value.Provider switch
        {
            DatabaseProvider.PostgreSql => ((decimal?)5432, "postgres", "postgres"),
            DatabaseProvider.MySql => (3306, string.Empty, "root"),
            DatabaseProvider.SqlServer => (1433, "master", "sa"),
            _ => (null, string.Empty, string.Empty),
        };
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (Validate() is { } problem)
        {
            Show(problem, error: true);
            return;
        }

        IsBusy = true;
        Show("Connecting…", error: false);
        try
        {
            var profile = Build();
            await ObjectExplorer.TestAsync(profile, profile.Password);
            Show("Connection successful.", error: false);
        }
        catch (IntrospectionException ex)
        {
            Show(ex.Message, error: true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (Validate() is { } problem)
        {
            Show(problem, error: true);
            if (string.IsNullOrWhiteSpace(Name))
            {
                TabIndex = 0;
            }

            return;
        }

        Saved = _store.Upsert(Build());
        Completed?.Invoke();
    }

    private ServerProfile Build() => new()
    {
        Id = _existing?.Id ?? Guid.NewGuid().ToString("N"),
        Name = Name.Trim(),
        Group = string.IsNullOrWhiteSpace(Group) ? "Servers" : Group.Trim(),
        Provider = SelectedProvider.Provider,
        Host = Blank(Host),
        Port = Port is { } port ? (int)port : null,
        MaintenanceDatabase = Blank(MaintenanceDatabase),
        Username = Blank(Username),
        Password = string.IsNullOrEmpty(Password) ? _existing?.Password : Password,
        SavePassword = SavePassword,
        FilePath = Blank(FilePath),
        Options = Blank(Options),
        Comment = Blank(Comment),
    };

    private string? Validate() =>
        string.IsNullOrWhiteSpace(Name) ? "Name is required (General tab)."
        : IsSqlite && string.IsNullOrWhiteSpace(FilePath) ? "Database file is required."
        : !IsSqlite && string.IsNullOrWhiteSpace(Host) ? "Host is required."
        : null;

    private void Show(string message, bool error)
    {
        Message = message;
        IsError = error;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
