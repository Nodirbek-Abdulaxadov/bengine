namespace Bengine.Introspection.Backup;

using System.Diagnostics;
using Bengine.Enums;
using Bengine.Introspection.Connection;
using Bengine.Introspection.Servers;

/// <summary>The archive format of a PostgreSQL dump.</summary>
public enum DumpFormat
{
    /// <summary>A plain SQL script (restored with psql).</summary>
    Plain,

    /// <summary>pg_dump's compressed custom archive (restored with pg_restore).</summary>
    Custom,

    /// <summary>A directory archive, one file per table.</summary>
    Directory,

    /// <summary>A tar archive.</summary>
    Tar,
}

/// <summary>What to dump with a native tool.</summary>
public sealed record DumpRequest
{
    /// <summary>Gets the output file (or directory, for <see cref="DumpFormat.Directory"/>).</summary>
    public required string OutputPath { get; init; }

    /// <summary>Gets the archive format (PostgreSQL; MySQL always writes SQL).</summary>
    public DumpFormat Format { get; init; } = DumpFormat.Plain;

    /// <summary>Gets a value indicating whether to dump only the schema.</summary>
    public bool SchemaOnly { get; init; }

    /// <summary>Gets a value indicating whether to dump only the data.</summary>
    public bool DataOnly { get; init; }

    /// <summary>Gets the tables to dump, or empty for all.</summary>
    public IReadOnlyList<string> Tables { get; init; } = [];

    /// <summary>Gets a value indicating whether the script drops objects before creating them.</summary>
    public bool Clean { get; init; }

    /// <summary>Gets a value indicating whether to leave out ownership and privileges (PostgreSQL).</summary>
    public bool NoOwner { get; init; }
}

/// <summary>What to restore with a native tool.</summary>
public sealed record RestoreRequest
{
    /// <summary>Gets the dump file (or directory archive).</summary>
    public required string InputPath { get; init; }

    /// <summary>Gets a value indicating whether to drop objects before recreating them (pg_restore).</summary>
    public bool Clean { get; init; }

    /// <summary>Gets a value indicating whether to leave out ownership (pg_restore).</summary>
    public bool NoOwner { get; init; }

    /// <summary>Gets a value indicating whether to stop at the first error.</summary>
    public bool StopOnError { get; init; } = true;
}

/// <summary>A prepared command line for a client tool.</summary>
/// <param name="FileName">The executable.</param>
/// <param name="Arguments">The arguments (passed as a list; nothing is shell-parsed).</param>
/// <param name="Environment">Environment variables (passwords travel here, never on the command line).</param>
/// <param name="InputFile">A file piped to the tool's standard input, or <c>null</c>.</param>
public sealed record ToolCommand(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, string? InputFile = null)
{
    /// <summary>Gets the command line to show (the password is in the environment, so it is never shown).</summary>
    public string Display => string.Join(' ', new[] { Path.GetFileName(FileName) }.Concat(Arguments.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a)))
        + (InputFile is null ? string.Empty : $" < \"{InputFile}\"");
}

/// <summary>
/// Dumps and restores through the databases' own client tools, the way
/// DataGrip and pgAdmin do: <c>pg_dump</c>/<c>pg_restore</c>/<c>psql</c> for
/// PostgreSQL and <c>mysqldump</c>/<c>mysql</c> (or the <c>mariadb-*</c>
/// equivalents) for MySQL, found on the PATH or in a configured folder. The
/// password goes through the environment (<c>PGPASSWORD</c>,
/// <c>MYSQL_PWD</c>), SSH-tunnelled servers are reached through the tunnel's
/// local port, and SSL settings carry over. SQL Server backs up and restores
/// on the server itself (<see cref="SqlServerBackupSql"/>), and SQLite copies
/// the file (<see cref="SqliteBackupSql"/>).
/// </summary>
public static class NativeTools
{
    /// <summary>Gets whether a provider dumps through external client tools.</summary>
    /// <param name="provider">The provider.</param>
    /// <returns><c>true</c> for PostgreSQL and MySQL.</returns>
    public static bool UsesClientTools(DatabaseProvider provider) => provider is DatabaseProvider.PostgreSql or DatabaseProvider.MySql;

    /// <summary>Finds a client tool: in a configured folder, on the PATH, then in the usual install folders.</summary>
    /// <param name="names">The tool's names, preferred first (<c>mysqldump</c>, <c>mariadb-dump</c>).</param>
    /// <param name="directory">A folder to look in first, or <c>null</c>.</param>
    /// <returns>The executable's path, or <c>null</c>.</returns>
    public static string? Find(IEnumerable<string> names, string? directory = null)
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var folders = new List<string>();
        if (!string.IsNullOrWhiteSpace(directory))
        {
            folders.Add(directory);
        }

        folders.AddRange((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        folders.AddRange(KnownFolders());
        foreach (var name in names)
        {
            foreach (var folder in folders)
            {
                var path = Path.Combine(folder, name + exe);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    /// <summary>Builds the dump command for a database.</summary>
    /// <param name="server">The server.</param>
    /// <param name="password">The password, or <c>null</c>.</param>
    /// <param name="database">The database.</param>
    /// <param name="request">What to dump.</param>
    /// <param name="toolDirectory">A folder holding the client tools, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels opening an SSH tunnel.</param>
    /// <returns>The command.</returns>
    /// <exception cref="IntrospectionException">The tool is not installed, or the provider has none.</exception>
    public static async Task<ToolCommand> DumpCommandAsync(ServerProfile server, string? password, string database, DumpRequest request, string? toolDirectory = null, CancellationToken cancellationToken = default)
    {
        var route = await SshTunnels.RouteAsync(server, cancellationToken).ConfigureAwait(false);
        switch (server.Provider)
        {
            case DatabaseProvider.PostgreSql:
            {
                var args = PostgresConnection(route, database);
                args.Add($"--file={request.OutputPath}");
                args.Add("--format=" + request.Format switch { DumpFormat.Custom => "c", DumpFormat.Directory => "d", DumpFormat.Tar => "t", _ => "p" });
                AddPostgresOptions(args, request.SchemaOnly, request.DataOnly, request.Clean, request.NoOwner);
                args.AddRange(request.Tables.Select(t => $"--table={t}"));
                args.Add("--verbose");
                return new ToolCommand(Require(["pg_dump"], toolDirectory), args, PostgresEnvironment(route, password));
            }

            case DatabaseProvider.MySql:
            {
                var tool = Require(["mysqldump", "mariadb-dump"], toolDirectory);
                var args = MySqlConnection(route, tool);
                args.Add($"--result-file={request.OutputPath}");
                args.AddRange(["--single-transaction", "--routines", "--triggers", "--hex-blob", "--default-character-set=utf8mb4"]);
                if (request.SchemaOnly)
                {
                    args.Add("--no-data");
                }

                if (request.DataOnly)
                {
                    args.Add("--no-create-info");
                    args.Add("--skip-triggers");
                }

                if (!request.Clean)
                {
                    args.Add("--skip-add-drop-table");
                }

                args.Add("--verbose");
                args.Add(database);
                args.AddRange(request.Tables);
                return new ToolCommand(tool, args, MySqlEnvironment(password));
            }

            default:
                throw new IntrospectionException($"{server.Provider} has no dump client; use a server backup or the Bengine SQL dump.");
        }
    }

    /// <summary>Builds the restore command for a dump: psql for SQL scripts, pg_restore for archives, mysql for MySQL.</summary>
    /// <param name="server">The server.</param>
    /// <param name="password">The password, or <c>null</c>.</param>
    /// <param name="database">The database to restore into.</param>
    /// <param name="request">What to restore.</param>
    /// <param name="toolDirectory">A folder holding the client tools, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancels opening an SSH tunnel.</param>
    /// <returns>The command.</returns>
    /// <exception cref="IntrospectionException">The tool is not installed, or the provider has none.</exception>
    public static async Task<ToolCommand> RestoreCommandAsync(ServerProfile server, string? password, string database, RestoreRequest request, string? toolDirectory = null, CancellationToken cancellationToken = default)
    {
        var route = await SshTunnels.RouteAsync(server, cancellationToken).ConfigureAwait(false);
        switch (server.Provider)
        {
            case DatabaseProvider.PostgreSql when DetectFormat(request.InputPath) == DumpFormat.Plain:
            {
                var args = PostgresConnection(route, database);
                args.Add($"--file={request.InputPath}");
                if (request.StopOnError)
                {
                    args.Add("--set=ON_ERROR_STOP=1");
                }

                args.Add("--echo-errors");
                return new ToolCommand(Require(["psql"], toolDirectory), args, PostgresEnvironment(route, password));
            }

            case DatabaseProvider.PostgreSql:
            {
                var args = PostgresConnection(route, database);
                AddPostgresOptions(args, schemaOnly: false, dataOnly: false, request.Clean, request.NoOwner);
                if (request.StopOnError)
                {
                    args.Add("--exit-on-error");
                }

                args.Add("--verbose");
                args.Add(request.InputPath);
                return new ToolCommand(Require(["pg_restore"], toolDirectory), args, PostgresEnvironment(route, password));
            }

            case DatabaseProvider.MySql:
            {
                var tool = Require(["mysql", "mariadb"], toolDirectory);
                var args = MySqlConnection(route, tool);
                args.Add("--default-character-set=utf8mb4");
                if (!request.StopOnError)
                {
                    args.Add("--force");
                }

                args.Add("--verbose");
                args.Add(database);
                return new ToolCommand(tool, args, MySqlEnvironment(password), request.InputPath);
            }

            default:
                throw new IntrospectionException($"{server.Provider} has no restore client; run the script or use a server restore.");
        }
    }

    /// <summary>Tells a PostgreSQL archive's format from its first bytes (or a directory).</summary>
    /// <param name="path">The file or directory.</param>
    /// <returns>The format.</returns>
    public static DumpFormat DetectFormat(string path)
    {
        if (System.IO.Directory.Exists(path))
        {
            return DumpFormat.Directory;
        }

        if (!File.Exists(path))
        {
            return DumpFormat.Plain;
        }

        using var stream = File.OpenRead(path);
        var head = new byte[262];
        var read = stream.Read(head, 0, head.Length);
        if (read >= 5 && head[0] == 'P' && head[1] == 'G' && head[2] == 'D' && head[3] == 'M' && head[4] == 'P')
        {
            return DumpFormat.Custom;
        }

        return read >= 262 && System.Text.Encoding.ASCII.GetString(head, 257, 5) == "ustar" ? DumpFormat.Tar : DumpFormat.Plain;
    }

    /// <summary>Runs a tool, streaming its output, and returns its exit code.</summary>
    /// <param name="command">The command.</param>
    /// <param name="output">Receives each line of output and errors.</param>
    /// <param name="cancellationToken">Kills the tool.</param>
    /// <returns>The exit code (0 on success).</returns>
    public static async Task<int> RunAsync(ToolCommand command, Action<string> output, CancellationToken cancellationToken = default)
    {
        var info = new ProcessStartInfo(command.FileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = command.InputFile is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in command.Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in command.Environment)
        {
            info.Environment[key] = value;
        }

        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output(e.Data);
            }
        };

        try
        {
            process.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IntrospectionException($"Could not start {command.FileName}: {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await using var kill = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        });

        if (command.InputFile is { } input)
        {
            try
            {
                await using (var file = File.OpenRead(input))
                {
                    await file.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
                }

                process.StandardInput.Close();
            }
            catch (IOException) when (process.HasExited)
            {
                // The tool stopped reading (it failed); its exit code says why.
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    /// <summary>Builds SQL Server's server-side backup (the path is on the database server).</summary>
    /// <param name="database">The database.</param>
    /// <param name="path">The backup file, on the server.</param>
    /// <returns>The statement; progress arrives as server messages.</returns>
    public static string SqlServerBackupSql(string database, string path) =>
        $"BACKUP DATABASE [{database.Replace("]", "]]", StringComparison.Ordinal)}] TO DISK = N'{path.Replace("'", "''", StringComparison.Ordinal)}' WITH COPY_ONLY, INIT, STATS = 10";

    /// <summary>Builds SQL Server's server-side restore, closing other connections to the database first.</summary>
    /// <param name="database">The database to restore (replaced).</param>
    /// <param name="path">The backup file, on the server.</param>
    /// <returns>The statements, to run on <c>master</c>.</returns>
    public static IReadOnlyList<string> SqlServerRestoreSql(string database, string path)
    {
        var name = $"[{database.Replace("]", "]]", StringComparison.Ordinal)}]";
        var literal = database.Replace("'", "''", StringComparison.Ordinal);
        return
        [
            $"IF DB_ID(N'{literal}') IS NOT NULL ALTER DATABASE {name} SET SINGLE_USER WITH ROLLBACK IMMEDIATE",
            $"RESTORE DATABASE {name} FROM DISK = N'{path.Replace("'", "''", StringComparison.Ordinal)}' WITH REPLACE, STATS = 10",
            $"ALTER DATABASE {name} SET MULTI_USER",
        ];
    }

    /// <summary>Builds SQLite's online copy of the open database into a new file.</summary>
    /// <param name="path">The new database file (must not exist).</param>
    /// <returns>The statement.</returns>
    public static string SqliteBackupSql(string path) => $"VACUUM INTO '{path.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>Restores a SQLite database from a database file with SQLite's online backup.</summary>
    /// <param name="server">The SQLite registration (its file is overwritten).</param>
    /// <param name="sourceFile">The backup database file.</param>
    public static void SqliteRestore(ServerProfile server, string sourceFile)
    {
        using var source = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sourceFile, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly }.ConnectionString);
        using var target = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionStringFactory.Build(server));
        source.Open();
        target.Open();
        source.BackupDatabase(target);
    }

    private static string Require(IReadOnlyList<string> names, string? directory) =>
        Find(names, directory) ?? throw new IntrospectionException(
            $"{names[0]} was not found. Install the {(names[0].StartsWith("pg", StringComparison.Ordinal) || names[0] == "psql" ? "PostgreSQL" : "MySQL")} client tools, or set their folder in the dialog.");

    private static List<string> PostgresConnection(ServerProfile server, string database) =>
    [
        $"--host={server.Host ?? "localhost"}",
        $"--port={server.Port ?? 5432}",
        $"--username={server.Username ?? Environment.UserName}",
        $"--dbname={database}",
        "--no-password",
    ];

    private static void AddPostgresOptions(List<string> args, bool schemaOnly, bool dataOnly, bool clean, bool noOwner)
    {
        if (schemaOnly)
        {
            args.Add("--schema-only");
        }

        if (dataOnly)
        {
            args.Add("--data-only");
        }

        if (clean)
        {
            args.Add("--clean");
            args.Add("--if-exists");
        }

        if (noOwner)
        {
            args.Add("--no-owner");
            args.Add("--no-privileges");
        }
    }

    private static Dictionary<string, string> PostgresEnvironment(ServerProfile server, string? password)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["PGAPPNAME"] = "Bengine" };
        if (!string.IsNullOrEmpty(password ?? server.Password))
        {
            env["PGPASSWORD"] = password ?? server.Password!;
        }

        if (!string.IsNullOrWhiteSpace(server.SslMode))
        {
            env["PGSSLMODE"] = server.SslMode.Trim().ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(server.SslRootCertificate))
        {
            env["PGSSLROOTCERT"] = server.SslRootCertificate;
        }

        if (!string.IsNullOrWhiteSpace(server.SslClientCertificate))
        {
            env["PGSSLCERT"] = server.SslClientCertificate;
        }

        if (!string.IsNullOrWhiteSpace(server.SslClientKey))
        {
            env["PGSSLKEY"] = server.SslClientKey;
        }

        return env;
    }

    private static List<string> MySqlConnection(ServerProfile server, string tool)
    {
        var args = new List<string>
        {
            $"--host={server.Host ?? "localhost"}",
            $"--port={server.Port ?? 3306}",
            $"--user={server.Username ?? Environment.UserName}",
            "--protocol=TCP",
        };
        var mariadb = Path.GetFileNameWithoutExtension(tool).StartsWith("mariadb", StringComparison.Ordinal);
        switch (server.SslMode?.Trim().ToLowerInvariant())
        {
            case "disable":
                args.Add(mariadb ? "--skip-ssl" : "--ssl-mode=DISABLED");
                break;
            case "require":
                args.Add(mariadb ? "--ssl" : "--ssl-mode=REQUIRED");
                break;
            case "verify-ca":
                args.AddRange(mariadb ? ["--ssl", "--ssl-verify-server-cert"] : ["--ssl-mode=VERIFY_CA"]);
                break;
            case "verify-full":
                args.AddRange(mariadb ? ["--ssl", "--ssl-verify-server-cert"] : ["--ssl-mode=VERIFY_IDENTITY"]);
                break;
        }

        if (!string.IsNullOrWhiteSpace(server.SslRootCertificate))
        {
            args.Add($"--ssl-ca={server.SslRootCertificate}");
        }

        if (!string.IsNullOrWhiteSpace(server.SslClientCertificate))
        {
            args.Add($"--ssl-cert={server.SslClientCertificate}");
        }

        if (!string.IsNullOrWhiteSpace(server.SslClientKey))
        {
            args.Add($"--ssl-key={server.SslClientKey}");
        }

        return args;
    }

    private static Dictionary<string, string> MySqlEnvironment(string? password) =>
        string.IsNullOrEmpty(password) ? new(StringComparer.Ordinal) : new(StringComparer.Ordinal) { ["MYSQL_PWD"] = password };

    private static IEnumerable<string> KnownFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (var root in new[] { Path.Combine(programFiles, "PostgreSQL"), Path.Combine(programFiles, "MySQL"), Path.Combine(programFiles, "MariaDB") })
            {
                if (System.IO.Directory.Exists(root))
                {
                    foreach (var version in System.IO.Directory.GetDirectories(root).OrderDescending())
                    {
                        yield return Path.Combine(version, "bin");
                    }
                }
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin";
            yield return "/usr/local/bin";
            yield return "/Applications/Postgres.app/Contents/Versions/latest/bin";
            foreach (var formula in new[] { "/opt/homebrew/opt/libpq/bin", "/opt/homebrew/opt/mysql-client/bin", "/usr/local/opt/libpq/bin", "/usr/local/opt/mysql-client/bin" })
            {
                yield return formula;
            }
        }
        else if (System.IO.Directory.Exists("/usr/lib/postgresql"))
        {
            foreach (var version in System.IO.Directory.GetDirectories("/usr/lib/postgresql").OrderDescending())
            {
                yield return Path.Combine(version, "bin");
            }
        }
    }
}
