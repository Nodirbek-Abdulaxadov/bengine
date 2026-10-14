namespace Bengine.Studio.ViewModels;

using System.Collections.ObjectModel;
using Avalonia.Threading;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Backup;
using Bengine.Introspection.Servers;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

/// <summary>How a database is dumped or restored.</summary>
public enum BackupMethod
{
    /// <summary>The provider's client tools (pg_dump / pg_restore / psql, mysqldump / mysql).</summary>
    ClientTool,

    /// <summary>SQL Server's BACKUP / RESTORE DATABASE, on the server's disk.</summary>
    ServerBackup,

    /// <summary>SQLite's file copy (VACUUM INTO / the online backup API).</summary>
    FileCopy,

    /// <summary>Bengine's portable SQL script (dump), or running any SQL script (restore).</summary>
    SqlScript,
}

/// <summary>One choice of the method list.</summary>
/// <param name="Method">The method.</param>
/// <param name="Label">What the list shows.</param>
public sealed record BackupMethodOption(BackupMethod Method, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// The Dump / Restore dialog of a database: through the client tools with the
/// command line shown (and the password kept out of it), SQL Server's
/// server-side backup, SQLite's file copy, or Bengine's own SQL script — with
/// a streamed log and cancel.
/// </summary>
public sealed partial class BackupViewModel : ObservableObject
{
    private readonly Func<Task<DatabaseSession>> _openSession;
    private readonly Func<Task<DatabaseSession>> _openMaster;
    private readonly string? _password;
    private readonly IDialogService? _dialogs;
    private readonly StudioSettings _settings;
    private CancellationTokenSource? _cancel;

    [ObservableProperty] private BackupMethodOption _method;
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private DumpFormat _format = DumpFormat.Plain;
    [ObservableProperty] private bool _schemaOnly;
    [ObservableProperty] private bool _dataOnly;
    [ObservableProperty] private bool _clean;
    [ObservableProperty] private bool _noOwner = true;
    [ObservableProperty] private bool _stopOnError = true;
    [ObservableProperty] private bool _singleTransaction;
    [ObservableProperty] private string? _toolDirectory;
    [ObservableProperty] private string _command = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private double _progress;

    /// <summary>Creates the dialog model.</summary>
    /// <param name="server">The server.</param>
    /// <param name="database">The database.</param>
    /// <param name="restore">Whether this restores (else dumps).</param>
    /// <param name="openSession">Opens a session on the database.</param>
    /// <param name="openMaster">Opens a session on the server's maintenance database (SQL Server restores).</param>
    /// <param name="password">The password in use.</param>
    /// <param name="dialogs">The dialogs.</param>
    /// <param name="settings">The Studio settings (client tool folder).</param>
    public BackupViewModel(ServerProfile server, string database, bool restore, Func<Task<DatabaseSession>> openSession, Func<Task<DatabaseSession>> openMaster, string? password, IDialogService? dialogs, StudioSettings settings)
    {
        Server = server;
        Database = database;
        IsRestore = restore;
        _openSession = openSession;
        _openMaster = openMaster;
        _password = password;
        _dialogs = dialogs;
        _settings = settings;
        _toolDirectory = settings.ToolDirectory;
        Methods = BuildMethods(server.Provider, restore);
        _method = Methods[0];
        _path = DefaultPath();
        UpdateCommand();
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event Action? Closed;

    /// <summary>Gets the server.</summary>
    public ServerProfile Server { get; }

    /// <summary>Gets the database.</summary>
    public string Database { get; }

    /// <summary>Gets a value indicating whether this restores.</summary>
    public bool IsRestore { get; }

    /// <summary>Gets the dialog title.</summary>
    public string Title => (IsRestore ? "Restore " : "Dump ") + $"{Database} — {Server.Name}";

    /// <summary>Gets the run button's caption.</summary>
    public string RunText => IsRestore ? "Restore" : "Dump";

    /// <summary>Gets the methods offered for the provider.</summary>
    public IReadOnlyList<BackupMethodOption> Methods { get; }

    /// <summary>Gets the PostgreSQL archive formats.</summary>
    public IReadOnlyList<DumpFormat> Formats { get; } = Enum.GetValues<DumpFormat>();

    /// <summary>Gets the log.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>Gets a value indicating whether the PostgreSQL format choice applies.</summary>
    public bool ShowFormat => !IsRestore && Method.Method == BackupMethod.ClientTool && Server.Provider == DatabaseProvider.PostgreSql;

    /// <summary>Gets a value indicating whether the content choices (schema only, data only) apply.</summary>
    public bool ShowContent => !IsRestore && Method.Method is BackupMethod.ClientTool or BackupMethod.SqlScript;

    /// <summary>Gets a value indicating whether the client tool folder applies.</summary>
    public bool ShowTools => Method.Method == BackupMethod.ClientTool;

    /// <summary>Gets a value indicating whether the script-run options apply.</summary>
    public bool ShowScriptOptions => IsRestore && Method.Method is BackupMethod.SqlScript or BackupMethod.ClientTool;

    /// <summary>Gets a hint about the path.</summary>
    public string PathHint => Method.Method == BackupMethod.ServerBackup
        ? "A path on the database server's disk (the server reads and writes it, not this computer)."
        : IsRestore ? "The dump file (or directory archive) to restore." : "Where the dump is written.";

    /// <summary>Gets a value indicating whether the path can be browsed for (it is on this computer).</summary>
    public bool CanBrowse => Method.Method != BackupMethod.ServerBackup;

    partial void OnMethodChanged(BackupMethodOption value)
    {
        Path = DefaultPath();
        OnPropertyChanged(nameof(ShowFormat));
        OnPropertyChanged(nameof(ShowContent));
        OnPropertyChanged(nameof(ShowTools));
        OnPropertyChanged(nameof(ShowScriptOptions));
        OnPropertyChanged(nameof(PathHint));
        OnPropertyChanged(nameof(CanBrowse));
        UpdateCommand();
    }

    partial void OnPathChanged(string value) => UpdateCommand();

    partial void OnFormatChanged(DumpFormat value)
    {
        if (!IsRestore && System.IO.Path.GetExtension(Path) is ".sql" or ".dump" or ".tar" or "")
        {
            Path = System.IO.Path.ChangeExtension(Path, Extension(value)).TrimEnd('.');
        }

        UpdateCommand();
    }

    partial void OnSchemaOnlyChanged(bool value)
    {
        if (value)
        {
            DataOnly = false;
        }

        UpdateCommand();
    }

    partial void OnDataOnlyChanged(bool value)
    {
        if (value)
        {
            SchemaOnly = false;
        }

        UpdateCommand();
    }

    partial void OnCleanChanged(bool value) => UpdateCommand();

    partial void OnNoOwnerChanged(bool value) => UpdateCommand();

    partial void OnStopOnErrorChanged(bool value) => UpdateCommand();

    partial void OnToolDirectoryChanged(string? value) => UpdateCommand();

    /// <summary>Runs the dump or restore.</summary>
    /// <returns>A task that completes when it ends.</returns>
    [RelayCommand]
    public async Task RunAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Path))
        {
            return;
        }

        if (IsRestore)
        {
            if (Server.ReadOnly)
            {
                Fail($"\"{Server.Name}\" is read-only.");
                return;
            }

            if (_dialogs is not null && !await _dialogs.ConfirmAsync("Restore", $"Restore into {Database} on {Server.Name}? Existing objects and rows may be replaced."))
            {
                return;
            }
        }

        if (!string.Equals(_settings.ToolDirectory, ToolDirectory, StringComparison.Ordinal))
        {
            _settings.ToolDirectory = string.IsNullOrWhiteSpace(ToolDirectory) ? null : ToolDirectory;
            TrySaveSettings();
        }

        IsBusy = true;
        IsError = false;
        Progress = 0;
        Log.Clear();
        _cancel = new CancellationTokenSource();
        var started = DateTime.Now;
        try
        {
            var message = IsRestore ? await RestoreAsync(_cancel.Token) : await DumpAsync(_cancel.Token);
            Status = $"{message} ({(DateTime.Now - started).TotalSeconds:N1} s)";
            Progress = 1;
        }
        catch (OperationCanceledException)
        {
            Fail("Cancelled.");
        }
        catch (Exception ex) when (ex is IntrospectionException or IOException or UnauthorizedAccessException)
        {
            Fail(ex.Message);
        }
        finally
        {
            IsBusy = false;
            _cancel = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (_cancel is { } cancel)
        {
            cancel.Cancel();
            Append("Cancelling…");
        }
        else
        {
            Closed?.Invoke();
        }
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_dialogs is null)
        {
            return;
        }

        var chosen = IsRestore
            ? Method.Method == BackupMethod.ClientTool && Server.Provider == DatabaseProvider.PostgreSql
                ? await _dialogs.OpenFileAsync("Dump to restore", ["sql", "dump", "backup", "tar"])
                : await _dialogs.OpenFileAsync("File to restore", Method.Method == BackupMethod.FileCopy ? ["db", "sqlite", "sqlite3"] : ["sql"])
            : Format == DumpFormat.Directory && ShowFormat
                ? await _dialogs.PickFolderAsync("Directory for the archive")
                : await _dialogs.SaveFileAsync(System.IO.Path.GetFileName(Path));
        if (chosen is not null)
        {
            Path = chosen;
        }
    }

    private async Task<string> DumpAsync(CancellationToken cancellationToken)
    {
        switch (Method.Method)
        {
            case BackupMethod.ClientTool:
            {
                var command = await NativeTools.DumpCommandAsync(Server, _password, Database, DumpRequest(), ToolDirectory, cancellationToken);
                return await RunToolAsync(command, cancellationToken);
            }

            case BackupMethod.ServerBackup:
            {
                await using var session = await _openSession();
                session.ServerMessage += Append;
                Append(NativeTools.SqlServerBackupSql(Database, Path));
                await session.Query.ExecuteAllAsync(NativeTools.SqlServerBackupSql(Database, Path), 1, cancellationToken);
                return $"Backed up {Database} to {Path} on the server.";
            }

            case BackupMethod.FileCopy:
            {
                await using var session = await _openSession();
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }

                Append(NativeTools.SqliteBackupSql(Path));
                await session.Query.ExecuteAllAsync(NativeTools.SqliteBackupSql(Path), 1, cancellationToken);
                return $"Copied the database to {Path} ({new FileInfo(Path).Length:N0} bytes).";
            }

            default:
            {
                await using var session = await _openSession();
                var progress = new Progress<string>(Append);
                var temp = Path + ".partial";
                SqlDumpSummary summary;
                await using (var writer = new StreamWriter(temp, append: false, new System.Text.UTF8Encoding(false)))
                {
                    summary = await SqlDump.WriteAsync(session, null, writer, new SqlDumpOptions { Schema = !DataOnly, Data = !SchemaOnly }, progress, cancellationToken);
                }

                File.Move(temp, Path, overwrite: true);
                return $"Wrote {summary.Tables} table(s) and {summary.Rows:N0} row(s) to {Path}.";
            }
        }
    }

    private async Task<string> RestoreAsync(CancellationToken cancellationToken)
    {
        switch (Method.Method)
        {
            case BackupMethod.ClientTool:
            {
                var command = await NativeTools.RestoreCommandAsync(Server, _password, Database, new RestoreRequest { InputPath = Path, Clean = Clean, NoOwner = NoOwner, StopOnError = StopOnError }, ToolDirectory, cancellationToken);
                return await RunToolAsync(command, cancellationToken);
            }

            case BackupMethod.ServerBackup:
            {
                await using var session = await _openMaster();
                session.ServerMessage += Append;
                foreach (var statement in NativeTools.SqlServerRestoreSql(Database, Path))
                {
                    Append(statement);
                    await session.Query.ExecuteAllAsync(statement, 1, cancellationToken);
                }

                return $"Restored {Database} from {Path}.";
            }

            case BackupMethod.FileCopy:
                await Task.Run(() => NativeTools.SqliteRestore(Server, Path), cancellationToken);
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                return $"Restored {Database} from {Path}.";

            default:
            {
                var script = await File.ReadAllTextAsync(Path, cancellationToken);
                await using var session = await _openSession();
                var progress = new Progress<ScriptProgress>(p =>
                {
                    Progress = p.Total == 0 ? 1 : (double)p.Done / p.Total;
                    Status = $"{p.Done:N0} / {p.Total:N0} statement(s)" + (p.Failed > 0 ? $", {p.Failed} failed" : string.Empty);
                    if (p.Message is { } line)
                    {
                        Append(line);
                    }
                });
                var result = await ScriptRunner.RunAsync(session, script, StopOnError, SingleTransaction, progress, cancellationToken);
                return $"Ran {result.Total:N0} statement(s)" + (result.Failed > 0 ? $"; {result.Failed} failed." : ".");
            }
        }
    }

    private async Task<string> RunToolAsync(ToolCommand command, CancellationToken cancellationToken)
    {
        Append("$ " + command.Display);
        var code = await NativeTools.RunAsync(command, line => Dispatcher.UIThread.Post(() => Append(line)), cancellationToken);
        if (code != 0)
        {
            throw new IntrospectionException($"{System.IO.Path.GetFileName(command.FileName)} exited with code {code} — see the log.");
        }

        return IsRestore ? $"Restored {Database}." : $"Dumped {Database} to {Path}.";
    }

    private DumpRequest DumpRequest() => new()
    {
        OutputPath = Path,
        Format = Server.Provider == DatabaseProvider.PostgreSql ? Format : DumpFormat.Plain,
        SchemaOnly = SchemaOnly,
        DataOnly = DataOnly,
        Clean = Clean,
        NoOwner = NoOwner,
    };

    private void UpdateCommand()
    {
        if (Method.Method != BackupMethod.ClientTool)
        {
            Command = Method.Method switch
            {
                BackupMethod.ServerBackup => IsRestore ? string.Join(";\n", NativeTools.SqlServerRestoreSql(Database, Path)) : NativeTools.SqlServerBackupSql(Database, Path),
                BackupMethod.FileCopy => IsRestore ? $"-- SQLite online backup: {Path} → {Server.FilePath}" : NativeTools.SqliteBackupSql(Path),
                _ => IsRestore ? $"-- Runs every statement of {System.IO.Path.GetFileName(Path)} on {Database}" : "-- Bengine SQL dump: tables, rows as INSERTs, indexes, keys, views",
            };
            return;
        }

        // Shows the command line without opening an SSH tunnel.
        var preview = Server with { SshHost = null };
        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                var command = IsRestore
                    ? await NativeTools.RestoreCommandAsync(preview, _password, Database, new RestoreRequest { InputPath = Path, Clean = Clean, NoOwner = NoOwner, StopOnError = StopOnError }, ToolDirectory)
                    : await NativeTools.DumpCommandAsync(preview, _password, Database, DumpRequest(), ToolDirectory);
                text = command.Display + (string.IsNullOrWhiteSpace(Server.SshHost) ? string.Empty : "\n-- (through the SSH tunnel's local port)");
            }
            catch (IntrospectionException ex)
            {
                text = "-- " + ex.Message;
            }

            Dispatcher.UIThread.Post(() => Command = text);
        });
    }

    private void Append(string line) => Log.Add(line);

    private void Fail(string message)
    {
        Status = message;
        IsError = true;
        Append(message);
    }

    private void TrySaveSettings()
    {
        try
        {
            _settings.Save();
        }
        catch (IOException)
        {
            // The folder is remembered for this session.
        }
    }

    private string DefaultPath()
    {
        if (Method.Method == BackupMethod.ServerBackup)
        {
            return $"/var/opt/mssql/data/{Database}.bak";
        }

        if (IsRestore)
        {
            return string.Empty;
        }

        var folder = Directory.Exists(_settings.ProjectFolder) ? _settings.ProjectFolder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var extension = Method.Method == BackupMethod.FileCopy ? "db" : Method.Method == BackupMethod.ClientTool && Server.Provider == DatabaseProvider.PostgreSql ? Extension(Format) : "sql";
        return System.IO.Path.Combine(folder, $"{Database}_{DateTime.Now:yyyyMMdd_HHmm}.{extension}".TrimEnd('.'));
    }

    private static string Extension(DumpFormat format) => format switch
    {
        DumpFormat.Custom => "dump",
        DumpFormat.Tar => "tar",
        DumpFormat.Directory => string.Empty,
        _ => "sql",
    };

    private static IReadOnlyList<BackupMethodOption> BuildMethods(DatabaseProvider provider, bool restore)
    {
        var methods = new List<BackupMethodOption>();
        switch (provider)
        {
            case DatabaseProvider.PostgreSql:
                methods.Add(new(BackupMethod.ClientTool, restore ? "pg_restore / psql" : "pg_dump"));
                break;
            case DatabaseProvider.MySql:
                methods.Add(new(BackupMethod.ClientTool, restore ? "mysql client" : "mysqldump"));
                break;
            case DatabaseProvider.SqlServer:
                methods.Add(new(BackupMethod.ServerBackup, restore ? "RESTORE DATABASE (on the server)" : "BACKUP DATABASE (on the server)"));
                break;
            case DatabaseProvider.Sqlite:
                methods.Add(new(BackupMethod.FileCopy, restore ? "Copy from a database file" : "Copy to a database file (VACUUM INTO)"));
                break;
        }

        methods.Add(new(BackupMethod.SqlScript, restore ? "Run a SQL script" : "Bengine SQL script (portable)"));
        return methods;
    }
}
