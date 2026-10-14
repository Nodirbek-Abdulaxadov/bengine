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

/// <summary>A color choice that marks a server.</summary>
/// <param name="Name">The display name.</param>
/// <param name="Hex">The <c>#rrggbb</c> value, or <c>null</c> for none.</param>
public sealed record ServerColorOption(string Name, string? Hex)
{
    /// <summary>Gets the swatch color (a neutral gray for "none").</summary>
    public string Swatch => Hex ?? "#2a3442";
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
    [ObservableProperty] private ServerColorOption _selectedColor;
    [ObservableProperty] private bool _readOnly;
    [ObservableProperty] private bool _useSsh;
    [ObservableProperty] private string _sshHost = string.Empty;
    [ObservableProperty] private decimal? _sshPort = 22;
    [ObservableProperty] private string _sshUser = string.Empty;
    [ObservableProperty] private string _sshKeyFile = string.Empty;
    [ObservableProperty] private string _sshPassword = string.Empty;
    [ObservableProperty] private string _sslMode = "(driver default)";
    [ObservableProperty] private string _sslRootCertificate = string.Empty;
    [ObservableProperty] private string _sslClientCertificate = string.Empty;
    [ObservableProperty] private string _sslClientKey = string.Empty;

    /// <summary>Creates the form, pre-filled from an existing registration when editing.</summary>
    /// <param name="store">The server store.</param>
    /// <param name="existing">The registration to edit, or <c>null</c> to register a new server.</param>
    public ServerFormViewModel(ServerStore store, ServerProfile? existing)
    {
        _store = store;
        _existing = existing;
        _selectedProvider = ProviderOptions[0];
        _selectedColor = ColorOptions[0];
        if (existing is null)
        {
            return;
        }

        _selectedColor = ColorOptions.FirstOrDefault(c => string.Equals(c.Hex, existing.Color, StringComparison.OrdinalIgnoreCase))
            ?? (existing.Color is null ? ColorOptions[0] : new ServerColorOption("Custom", existing.Color));
        _readOnly = existing.ReadOnly;
        _useSsh = !string.IsNullOrWhiteSpace(existing.SshHost);
        _sshHost = existing.SshHost ?? string.Empty;
        _sshPort = existing.SshPort ?? 22;
        _sshUser = existing.SshUser ?? string.Empty;
        _sshKeyFile = existing.SshKeyFile ?? string.Empty;
        _sslMode = existing.SslMode ?? "(driver default)";
        _sslRootCertificate = existing.SslRootCertificate ?? string.Empty;
        _sslClientCertificate = existing.SslClientCertificate ?? string.Empty;
        _sslClientKey = existing.SslClientKey ?? string.Empty;
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
        new(DatabaseProvider.Oracle, "Oracle Database"),
    ];

    /// <summary>Gets the color choices (DataGrip-like: red for production, green for local…).</summary>
    public IReadOnlyList<ServerColorOption> ColorOptions { get; } =
    [
        new("None", null),
        new("Red", "#e05d5d"),
        new("Orange", "#e0862f"),
        new("Yellow", "#d9b23a"),
        new("Green", "#3ccf7e"),
        new("Blue", "#4f8cff"),
        new("Purple", "#a779e9"),
    ];

    /// <summary>Gets the SSL mode choices.</summary>
    public IReadOnlyList<string> SslModes { get; } = ["(driver default)", "disable", "prefer", "require", "verify-ca", "verify-full"];

    /// <summary>Gets where saved passwords are kept.</summary>
    public string PasswordStorage => _store.Credentials is { } keyring ? $"Saved passwords go to the {keyring.Name}." : "Saved passwords go to the server list file (owner-only).";

    /// <summary>Gets the SSH password watermark.</summary>
    public string SshPasswordHint => _existing?.SshPassword is { Length: > 0 } ? "(saved — leave blank to keep)" : "password, or the key's passphrase";

    /// <summary>Gets the dialog title.</summary>
    public string Title => _existing is null ? "Register — Server" : $"{_existing.Name} — Properties";

    /// <summary>Gets the password watermark (hints that a saved password is kept).</summary>
    public string PasswordHint => _existing?.Password is { Length: > 0 } ? "(saved — leave blank to keep)" : string.Empty;

    /// <summary>Gets whether the provider is SQLite (file-based).</summary>
    public bool IsSqlite => SelectedProvider.Provider == DatabaseProvider.Sqlite;

    /// <summary>Gets whether the provider is a network server.</summary>
    public bool IsNetwork => !IsSqlite;

    /// <summary>Gets the caption of the database field: Oracle connects to a service (a pluggable database).</summary>
    public string DatabaseLabel => SelectedProvider.Provider == DatabaseProvider.Oracle ? "Service name" : "Maintenance database";

    /// <summary>Gets the saved registration once <see cref="Save"/> succeeds.</summary>
    public ServerProfile? Saved { get; private set; }

    /// <summary>Raised when the form saved successfully and the dialog should close.</summary>
    public event Action? Completed;

    partial void OnSelectedProviderChanged(ProviderOption value)
    {
        OnPropertyChanged(nameof(IsSqlite));
        OnPropertyChanged(nameof(IsNetwork));
        OnPropertyChanged(nameof(DatabaseLabel));
        if (_existing is not null)
        {
            return;
        }

        (Port, MaintenanceDatabase, Username) = value.Provider switch
        {
            DatabaseProvider.PostgreSql => ((decimal?)5432, "postgres", "postgres"),
            DatabaseProvider.MySql => (3306, string.Empty, "root"),
            DatabaseProvider.SqlServer => (1433, "master", "sa"),
            DatabaseProvider.Oracle => (1521, "FREEPDB1", "system"),
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
        Bengine.Introspection.Connection.SshTunnels.Close(Saved.Id);
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
        Color = SelectedColor.Hex,
        ReadOnly = ReadOnly,
        SshHost = UseSsh ? Blank(SshHost) : null,
        SshPort = UseSsh && SshPort is { } sshPort ? (int)sshPort : null,
        SshUser = UseSsh ? Blank(SshUser) : null,
        SshKeyFile = UseSsh ? Blank(SshKeyFile) : null,
        SshPassword = !UseSsh ? null : string.IsNullOrEmpty(SshPassword) ? _existing?.SshPassword : SshPassword,
        SslMode = SslMode.StartsWith('(') ? null : SslMode,
        SslRootCertificate = Blank(SslRootCertificate),
        SslClientCertificate = Blank(SslClientCertificate),
        SslClientKey = Blank(SslClientKey),
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
