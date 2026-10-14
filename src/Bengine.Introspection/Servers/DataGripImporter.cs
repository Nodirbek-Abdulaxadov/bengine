namespace Bengine.Introspection.Servers;

using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Bengine.Enums;

/// <summary>A data source that can be imported, with the file it was read from.</summary>
/// <param name="Server">The registration to import (without a password).</param>
/// <param name="SourceFile">The <c>dataSources.xml</c> it came from.</param>
public sealed record DataGripCandidate(ServerProfile Server, string SourceFile);

/// <summary>A data source that cannot be imported, and why.</summary>
/// <param name="Name">The data source name.</param>
/// <param name="SourceFile">The <c>dataSources.xml</c> it came from.</param>
/// <param name="Reason">Why it was skipped (for example an unsupported driver).</param>
public sealed record DataGripSkipped(string Name, string SourceFile, string Reason);

/// <summary>The outcome of reading DataGrip configurations.</summary>
/// <param name="Servers">The importable data sources.</param>
/// <param name="Skipped">The data sources that were skipped.</param>
/// <param name="Files">The configuration files that were read.</param>
public sealed record DataGripImport(
    IReadOnlyList<DataGripCandidate> Servers,
    IReadOnlyList<DataGripSkipped> Skipped,
    IReadOnlyList<string> Files);

/// <summary>
/// Reads data sources defined in DataGrip (and the other JetBrains IDEs that
/// bundle the Database Tools plugin, such as Rider and IntelliJ IDEA) so they can
/// be imported into Bengine. The format was confirmed from DataGrip's own
/// serializer: a <c>dataSources.xml</c> holds <c>&lt;data-source&gt;</c> elements
/// whose <c>name</c>, <c>group</c>, and <c>uuid</c> are attributes and whose
/// <c>jdbc-url</c> and <c>driver-ref</c> are child elements; the sibling
/// <c>dataSources.local.xml</c> holds the per-user <c>user-name</c>, matched by
/// <c>uuid</c>. Global data sources live in the IDE's <c>options</c> directory,
/// project ones in the project's <c>.idea</c> directory. Passwords are kept in
/// the OS keyring or DataGrip's encrypted store, never in these files, so
/// imported servers prompt on first connect. The files are only read.
/// </summary>
public static class DataGripImporter
{
    private const string DefaultGroup = "DataGrip";

    /// <summary>
    /// Finds DataGrip/JetBrains data-source files: every IDE's global
    /// <c>options/dataSources.xml</c>, the projects under <c>~/DataGripProjects</c>,
    /// and — when given — an extra project directory, <c>.idea</c> directory, or file.
    /// </summary>
    /// <param name="extraPath">An optional project directory, <c>.idea</c> directory, or <c>dataSources.xml</c> path.</param>
    /// <returns>The existing <c>dataSources.xml</c> files.</returns>
    public static IReadOnlyList<string> FindConfigFiles(string? extraPath = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configRoots = new[]
        {
            Path.Combine(appData, "JetBrains"),
            Path.Combine(home, ".config", "JetBrains"),
            Path.Combine(home, "Library", "Application Support", "JetBrains"),
        };

        var files = new List<string>();
        foreach (var root in configRoots.Distinct().Where(Directory.Exists))
        {
            foreach (var ide in Directory.EnumerateDirectories(root))
            {
                files.Add(Path.Combine(ide, "options", "dataSources.xml"));
            }
        }

        var projects = Path.Combine(home, "DataGripProjects");
        if (Directory.Exists(projects))
        {
            files.AddRange(Directory.EnumerateDirectories(projects).Select(p => Path.Combine(p, ".idea", "dataSources.xml")));
        }

        if (!string.IsNullOrWhiteSpace(extraPath))
        {
            var path = Path.GetFullPath(extraPath.Trim());
            files.Add(File.Exists(path) ? path
                : Path.GetFileName(path) == ".idea" ? Path.Combine(path, "dataSources.xml")
                : Path.Combine(path, ".idea", "dataSources.xml"));
        }

        return files.Where(File.Exists).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads data sources from the given files (or from <see cref="FindConfigFiles"/>).</summary>
    /// <param name="files">The <c>dataSources.xml</c> files to read.</param>
    /// <returns>The importable and skipped data sources.</returns>
    public static DataGripImport Read(IEnumerable<string>? files = null)
    {
        var list = (files ?? FindConfigFiles()).ToList();
        var servers = new List<DataGripCandidate>();
        var skipped = new List<DataGripSkipped>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in list)
        {
            XDocument shared;
            try
            {
                shared = Load(file);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                skipped.Add(new DataGripSkipped(Path.GetFileName(file), file, $"Unreadable: {ex.Message}"));
                continue;
            }

            var users = ReadLocalUsers(Path.Combine(Path.GetDirectoryName(file)!, "dataSources.local.xml"));
            var projectDir = ProjectDirectoryOf(file);

            foreach (var element in shared.Descendants("data-source"))
            {
                var name = (string?)element.Attribute("name") ?? "(unnamed)";
                var uuid = (string?)element.Attribute("uuid");
                if (uuid is not null && !seen.Add(uuid))
                {
                    continue;
                }

                var url = element.Element("jdbc-url")?.Value.Trim();
                if (string.IsNullOrEmpty(url))
                {
                    skipped.Add(new DataGripSkipped(name, file, "No JDBC URL."));
                    continue;
                }

                var user = uuid is not null && users.TryGetValue(uuid, out var u) ? u : null;
                var group = (string?)element.Attribute("group");
                if (TryMap(name, string.IsNullOrWhiteSpace(group) ? DefaultGroup : group, ExpandMacros(url, projectDir), user, out var profile, out var reason))
                {
                    servers.Add(new DataGripCandidate(profile!, file));
                }
                else
                {
                    skipped.Add(new DataGripSkipped(name, file, reason!));
                }
            }
        }

        return new DataGripImport(servers, skipped, list);
    }

    /// <summary>Maps a DataGrip JDBC URL to a server registration.</summary>
    /// <param name="name">The data source name.</param>
    /// <param name="group">The server group.</param>
    /// <param name="url">The JDBC URL, with path macros already expanded.</param>
    /// <param name="user">The login name from <c>dataSources.local.xml</c>, if any.</param>
    /// <param name="profile">The registration, when the URL is supported.</param>
    /// <param name="reason">Why the URL is not supported, otherwise.</param>
    /// <returns><c>true</c> if the URL maps to a supported provider.</returns>
    public static bool TryMap(string name, string group, string url, string? user, out ServerProfile? profile, out string? reason)
    {
        profile = null;
        reason = null;

        // DataGrip's MongoDB data sources carry a plain mongodb:// URL.
        var mongo = url.StartsWith("jdbc:mongodb", StringComparison.OrdinalIgnoreCase) ? url[5..] : url;
        if (mongo.StartsWith("mongodb://", StringComparison.OrdinalIgnoreCase) || mongo.StartsWith("mongodb+srv://", StringComparison.OrdinalIgnoreCase))
        {
            return TryMapMongo(name, group, mongo, user, out profile, out reason);
        }

        if (!url.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase))
        {
            reason = "Not a JDBC URL.";
            return false;
        }

        var body = url[5..];
        var scheme = body.Split(':', 2)[0].ToLowerInvariant();
        switch (scheme)
        {
            case "postgresql":
            {
                var (host, port, database, query) = Network(body["postgresql:".Length..], ';');
                var options = Query(query).TryGetValue("sslmode", out var ssl) && SslMode(ssl) is { } mode ? $"SSL Mode={mode}" : null;
                profile = Network(name, group, DatabaseProvider.PostgreSql, host, port, database ?? "postgres", user ?? Query(query).GetValueOrDefault("user"), options);
                return true;
            }

            case "mysql" or "mariadb":
            {
                var (host, port, database, query) = Network(body[(scheme.Length + 1)..], ';');
                profile = Network(name, group, DatabaseProvider.MySql, host, port, database, user ?? Query(query).GetValueOrDefault("user"), null);
                return true;
            }

            case "sqlserver":
                profile = SqlServer(name, group, body["sqlserver:".Length..], user);
                return true;

            case "jtds" when body.StartsWith("jtds:sqlserver:", StringComparison.OrdinalIgnoreCase):
            {
                var (host, port, database, _) = Network(body["jtds:sqlserver:".Length..], ';');
                profile = Network(name, group, DatabaseProvider.SqlServer, host, port, database, user, null);
                return true;
            }

            case "oracle":
                return TryMapOracle(name, group, body, user, out profile, out reason);

            case "sqlite":
            {
                var path = body["sqlite:".Length..].Split('?', 2)[0];
                if (path.Length == 0 || path.StartsWith(":memory:", StringComparison.Ordinal))
                {
                    reason = "In-memory SQLite database.";
                    return false;
                }

                profile = new ServerProfile { Name = name, Group = group, Provider = DatabaseProvider.Sqlite, FilePath = path };
                return true;
            }

            default:
                reason = $"Unsupported driver ({scheme}).";
                return false;
        }
    }

    // mongodb://[user@]host:port/db?options: one plain host becomes host and port; SRV and replica-set URLs are kept whole.
    private static bool TryMapMongo(string name, string group, string url, string? user, out ServerProfile? profile, out string? reason)
    {
        profile = null;
        reason = null;
        MongoDB.Driver.MongoUrl parsed;
        try
        {
            parsed = MongoDB.Driver.MongoUrl.Create(url);
        }
        catch (Exception ex) when (ex is MongoDB.Driver.MongoConfigurationException or ArgumentException or FormatException)
        {
            reason = $"Invalid MongoDB URL ({ex.Message}).";
            return false;
        }

        var login = user ?? parsed.Username;
        var authentication = parsed.AuthenticationSource ?? (string.IsNullOrEmpty(parsed.DatabaseName) ? "admin" : parsed.DatabaseName);
        var single = parsed.Scheme == MongoDB.Driver.Core.Configuration.ConnectionStringScheme.MongoDB && parsed.Servers.Count() == 1;
        if (single)
        {
            // The URL's options (other than credentials and the auth database) carry over.
            var query = url.IndexOf('?', StringComparison.Ordinal) is var q and >= 0 ? url[(q + 1)..] : string.Empty;
            var options = string.Join('\n', query.Split('&', StringSplitOptions.RemoveEmptyEntries).Where(o => !o.StartsWith("authSource=", StringComparison.OrdinalIgnoreCase)));
            profile = Network(name, group, DatabaseProvider.MongoDb, parsed.Server.Host, parsed.Server.Port, authentication, login, options.Length == 0 ? null : options);
        }
        else
        {
            // Without the credentials: those come from the registration.
            var builder = new MongoDB.Driver.MongoUrlBuilder(url) { Username = null, Password = null };
            profile = new ServerProfile { Name = name, Group = group, Provider = DatabaseProvider.MongoDb, Host = builder.ToString(), MaintenanceDatabase = authentication, Username = login };
        }

        return true;
    }

    // jdbc:oracle:thin:[user/pass]@//host:port/service, @host:port:SID, or @(DESCRIPTION=…); a TNS alias cannot be resolved here.
    private static bool TryMapOracle(string name, string group, string body, string? user, out ServerProfile? profile, out string? reason)
    {
        profile = null;
        reason = null;
        var at = body.IndexOf('@', StringComparison.Ordinal);
        if (at < 0)
        {
            reason = "No host in the Oracle URL.";
            return false;
        }

        var target = body[(at + 1)..].Trim();
        string? host;
        int? port = null;
        string? service;
        if (target.StartsWith('('))
        {
            string? Value(string key) => System.Text.RegularExpressions.Regex.Match(target, key + @"\s*=\s*([^)\s]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;
            host = Value("HOST");
            port = int.TryParse(Value("PORT"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : null;
            service = Value("SERVICE_NAME") ?? Value("SID");
        }
        else
        {
            target = target.TrimStart('/').Split('?', 2)[0];
            var slash = target.IndexOf('/', StringComparison.Ordinal);
            var parts = (slash >= 0 ? target[..slash] : target).Split(':');
            host = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var p))
            {
                port = p;
            }

            service = slash >= 0 ? target[(slash + 1)..] : parts.Length > 2 ? parts[2] : null;
        }

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(service))
        {
            reason = "Oracle TNS alias or incomplete URL (set host, port, and service by hand).";
            return false;
        }

        profile = Network(name, group, DatabaseProvider.Oracle, host, port, service, user, null);
        return true;
    }

    private static ServerProfile Network(string name, string group, DatabaseProvider provider, string host, int? port, string? database, string? user, string? options) => new()
    {
        Name = name,
        Group = group,
        Provider = provider,
        Host = host,
        Port = port,
        MaintenanceDatabase = database,
        Username = string.IsNullOrEmpty(user) ? null : user,
        Options = options,
    };

    // //host[:port][,host2…][/database][?query] — the first host wins.
    private static (string Host, int? Port, string? Database, string? Query) Network(string rest, char propertySeparator)
    {
        string? query = null;
        var q = rest.IndexOfAny(['?', propertySeparator]);
        if (q >= 0)
        {
            query = rest[(q + 1)..];
            rest = rest[..q];
        }

        if (!rest.StartsWith("//", StringComparison.Ordinal))
        {
            // jdbc:postgresql:dbname — local default host.
            return ("localhost", null, rest.Length == 0 ? null : Uri.UnescapeDataString(rest), query);
        }

        rest = rest[2..];
        var slash = rest.IndexOf('/');
        var authority = slash >= 0 ? rest[..slash] : rest;
        var database = slash >= 0 && slash + 1 < rest.Length ? Uri.UnescapeDataString(rest[(slash + 1)..]) : null;
        var first = authority.Split(',')[0];
        if (first.Contains('@', StringComparison.Ordinal))
        {
            first = first[(first.LastIndexOf('@') + 1)..];
        }

        var (host, port) = HostPort(first);
        return (host, port, database, query);
    }

    // jdbc:sqlserver://host[\instance][:port][;databaseName=…;user=…]
    private static ServerProfile SqlServer(string name, string group, string rest, string? user)
    {
        var parts = rest.TrimStart('/').Split(';');
        var (host, port) = HostPort(parts[0]);
        var props = parts.Skip(1)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);
        var database = props.GetValueOrDefault("databaseName") ?? props.GetValueOrDefault("database");
        var options = new List<string>();
        if (props.TryGetValue("trustServerCertificate", out var trust))
        {
            options.Add($"TrustServerCertificate={trust}");
        }

        if (props.TryGetValue("encrypt", out var encrypt))
        {
            options.Add($"Encrypt={encrypt}");
        }

        return Network(name, group, DatabaseProvider.SqlServer, host, port, database ?? "master",
            user ?? props.GetValueOrDefault("user"), options.Count == 0 ? null : string.Join(";", options));
    }

    private static (string Host, int? Port) HostPort(string value)
    {
        value = value.Trim();
        if (value.Length == 0)
        {
            return ("localhost", null);
        }

        // [ipv6]:port
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            var host6 = value[1..close];
            return close + 2 <= value.Length && value[close + 1] == ':' && int.TryParse(value[(close + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p6)
                ? (host6, p6)
                : (host6, null);
        }

        var colon = value.LastIndexOf(':');
        return colon > 0 && int.TryParse(value[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? (value[..colon], port)
            : (value, null);
    }

    private static Dictionary<string, string> Query(string? query) =>
        (query ?? string.Empty)
            .Split(['&', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.Last()[1]), StringComparer.OrdinalIgnoreCase);

    private static string? SslMode(string libpq) => libpq.ToLowerInvariant() switch
    {
        "disable" => "Disable",
        "allow" => "Allow",
        "prefer" => "Prefer",
        "require" => "Require",
        "verify-ca" => "VerifyCA",
        "verify-full" => "VerifyFull",
        _ => null,
    };

    // DataGrip collapses paths in JDBC URLs into IDE path macros before saving.
    private static string ExpandMacros(string url, string? projectDir)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = url.Replace("$USER_HOME$", home, StringComparison.Ordinal);
        if (projectDir is not null)
        {
            expanded = expanded
                .Replace("$PROJECT_DIR$", projectDir, StringComparison.Ordinal)
                .Replace("$ProjectFileDir$", projectDir, StringComparison.Ordinal);
        }

        return expanded;
    }

    // <project>/.idea/dataSources.xml → <project>; a global options file has no project.
    private static string? ProjectDirectoryOf(string file)
    {
        var dir = Path.GetDirectoryName(file);
        return dir is not null && Path.GetFileName(dir) == ".idea" ? Path.GetDirectoryName(dir) : null;
    }

    private static Dictionary<string, string> ReadLocalUsers(string localFile)
    {
        var users = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(localFile))
        {
            return users;
        }

        try
        {
            foreach (var element in Load(localFile).Descendants("data-source"))
            {
                if ((string?)element.Attribute("uuid") is { } uuid && element.Element("user-name")?.Value is { Length: > 0 } user)
                {
                    users[uuid] = user;
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            // A missing or unreadable local file only loses the user names.
        }

        return users;
    }

    // DTDs are refused, so a crafted file cannot pull in external entities.
    private static XDocument Load(string file)
    {
        using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }
}
