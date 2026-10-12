namespace Bengine.Api;

using System.Text.Json;
using Bengine.Enums;
using Bengine.Introspection;
using Bengine.Introspection.Export;
using Bengine.Introspection.Model;
using Bengine.Introspection.Servers;
using Bengine.Introspection.Tree;
using Bengine.Models;
using Bengine.Serialization;

/// <summary>
/// HTTP surface of the database explorer: the registered-server list (add, edit,
/// remove, test, connect, import from pgAdmin), the lazily loaded object tree
/// with per-node properties and SQL, and the per-database views — data browsing
/// and editing, the SQL console, structure, and export. Every data endpoint
/// targets either a registered server plus database, or a raw provider plus
/// connection string, and opens a short-lived connection for the request. These
/// endpoints act with the credentials they are given and the API listens on
/// localhost only; do not expose them to untrusted callers.
/// </summary>
public static class DatabaseEndpoints
{
    /// <summary>Maps the explorer endpoints under <c>/api/servers</c>, <c>/api/tree</c>, and <c>/api/db</c>.</summary>
    /// <param name="app">The web application to map onto.</param>
    public static void MapDatabaseEndpoints(this WebApplication app)
    {
        MapServers(app);
        MapTree(app);
        MapData(app);
    }

    // ------------------------------------------------------------- servers

    private static void MapServers(WebApplication app)
    {
        app.MapGet("/api/servers", (ServerRegistry registry) =>
            Results.Json(registry.Store.List().Select(s => ToDto(s, registry))));

        app.MapPost("/api/servers", (ServerRequest request, ServerRegistry registry) =>
        {
            if (!TryBuildProfile(request, registry, out var profile, out var error))
            {
                return Results.BadRequest(new { error });
            }

            var stored = registry.Store.Upsert(profile);
            return Results.Json(ToDto(stored, registry));
        });

        app.MapDelete("/api/servers/{id}", (string id, ServerRegistry registry) =>
        {
            registry.Disconnect(id);
            return registry.Store.Remove(id) ? Results.NoContent() : Results.NotFound(new { error = "No such server." });
        });

        // Test a registration form before (or without) saving it.
        app.MapPost("/api/servers/test", async (ServerRequest request, ServerRegistry registry) =>
        {
            if (!TryBuildProfile(request, registry, out var profile, out var error))
            {
                return Results.BadRequest(new { error });
            }

            try
            {
                await ObjectExplorer.TestAsync(profile, registry.PasswordFor(profile));
                return Results.Json(new { ok = true });
            }
            catch (IntrospectionException ex)
            {
                return Fail(ex);
            }
        });

        app.MapPost("/api/servers/{id}/connect", async (string id, ConnectRequest request, ServerRegistry registry) =>
        {
            if (registry.Store.Get(id) is not { } server)
            {
                return Results.NotFound(new { error = "No such server." });
            }

            var typed = string.IsNullOrEmpty(request.Password) ? null : request.Password;
            try
            {
                await ObjectExplorer.TestAsync(server, typed ?? registry.PasswordFor(server));
            }
            catch (IntrospectionException ex)
            {
                return Fail(ex);
            }

            if (typed is not null && request.SavePassword)
            {
                server = registry.Store.Upsert(server with { Password = typed, SavePassword = true });
            }

            registry.MarkConnected(server, typed);
            return Results.Json(ToDto(server, registry));
        });

        app.MapPost("/api/servers/{id}/disconnect", (string id, ServerRegistry registry) =>
        {
            registry.Disconnect(id);
            return Results.Json(new { connected = false });
        });

        app.MapGet("/api/servers/pgadmin", (ServerRegistry registry) =>
        {
            var path = PgAdminImporter.FindDatabase();
            if (path is null)
            {
                return Results.Json(new { found = false, servers = Array.Empty<object>() });
            }

            try
            {
                var existing = registry.Store.List();
                var servers = PgAdminImporter.Read(path).Select(s => new
                {
                    s.Name,
                    s.Group,
                    s.Host,
                    s.Port,
                    s.MaintenanceDatabase,
                    s.Username,
                    exists = existing.Any(e => SameEndpoint(e, s)),
                });
                return Results.Json(new { found = true, path, servers });
            }
            catch (IntrospectionException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        // DataGrip (and Rider/IntelliJ) data sources: the IDEs' global options plus
        // ~/DataGripProjects, and optionally one project directory given by ?path=.
        app.MapGet("/api/servers/datagrip", (string? path, ServerRegistry registry) =>
        {
            var existing = registry.Store.List();
            var result = DataGripImporter.Read(DataGripImporter.FindConfigFiles(path));
            return Results.Json(new
            {
                files = result.Files,
                servers = result.Servers.Select(c => new
                {
                    c.Server.Name,
                    c.Server.Group,
                    provider = c.Server.Provider.ToString(),
                    c.Server.Endpoint,
                    c.Server.MaintenanceDatabase,
                    c.Server.Username,
                    c.SourceFile,
                    exists = existing.Any(e => SameEndpoint(e, c.Server)),
                }),
                skipped = result.Skipped,
            });
        });

        app.MapPost("/api/servers/datagrip/import", (string? path, ServerRegistry registry) =>
        {
            var existing = registry.Store.List();
            var imported = 0;
            foreach (var candidate in DataGripImporter.Read(DataGripImporter.FindConfigFiles(path)).Servers)
            {
                if (existing.Any(e => SameEndpoint(e, candidate.Server)))
                {
                    continue;
                }

                registry.Store.Upsert(candidate.Server);
                imported++;
            }

            return Results.Json(new { imported, servers = registry.Store.List().Select(s => ToDto(s, registry)) });
        });

        app.MapPost("/api/servers/pgadmin/import", (ServerRegistry registry) =>
        {
            try
            {
                var existing = registry.Store.List();
                var imported = 0;
                foreach (var server in PgAdminImporter.Read())
                {
                    if (existing.Any(e => SameEndpoint(e, server)))
                    {
                        continue;
                    }

                    registry.Store.Upsert(server);
                    imported++;
                }

                return Results.Json(new { imported, servers = registry.Store.List().Select(s => ToDto(s, registry)) });
            }
            catch (IntrospectionException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
    }

    // ---------------------------------------------------------------- tree

    private static void MapTree(WebApplication app)
    {
        app.MapPost("/api/tree/children", async (TreeRequest request, ServerRegistry registry) =>
        {
            if (registry.Store.Get(request.ServerId) is not { } server)
            {
                return Results.NotFound(new { error = "No such server." });
            }

            try
            {
                var nodes = await ObjectExplorer.GetChildrenAsync(server, registry.PasswordFor(server), request.Node);
                registry.MarkConnected(server, null);
                return Results.Json(nodes);
            }
            catch (IntrospectionException ex)
            {
                return Fail(ex);
            }
        });

        app.MapPost("/api/tree/properties", async (TreeRequest request, ServerRegistry registry) =>
        {
            if (registry.Store.Get(request.ServerId) is not { } server)
            {
                return Results.NotFound(new { error = "No such server." });
            }

            try
            {
                return Results.Json(await ObjectExplorer.GetPropertiesAsync(server, registry.PasswordFor(server), request.Node));
            }
            catch (IntrospectionException ex)
            {
                return Fail(ex);
            }
        });
    }

    // ---------------------------------------------------------------- data

    private static void MapData(WebApplication app)
    {
        app.MapPost("/api/db/test", (TargetRequest request, ServerRegistry registry) =>
            WithSession(request, registry, _ => Task.FromResult(Results.Json(new { canConnect = true }))));

        app.MapPost("/api/db/databases", (TargetRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
                Results.Json(new { databases = await session.Explorer.GetDatabasesAsync() })));

        app.MapPost("/api/db/schemas", (TargetRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
                Results.Json(new { schemas = await session.Explorer.GetSchemasAsync() })));

        app.MapPost("/api/db/tables", (TargetRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
            {
                var tables = await session.Explorer.GetTablesAsync(Blank(request.Schema));
                return Results.Json(new { tables = tables.Select(t => new { t.Name, t.Schema, kind = t.Kind.ToString() }) });
            }));

        app.MapPost("/api/db/introspect", (TargetRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
            {
                var schema = await session.Introspector.IntrospectAsync(Blank(request.Schema));
                using var document = JsonDocument.Parse(SchemaSerializer.ToJson(schema));
                return Results.Json(new { tableCount = schema.Tables.Count, schema = document.RootElement.Clone() });
            }));

        app.MapPost("/api/db/data", (DataRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
            {
                var page = await session.Data.QueryAsync(new TableRef(request.Table, Blank(request.Schema)), ToQuery(request.Query));
                return Results.Json(new { page.Columns, page.ColumnTypes, rows = CellFormatter.Rows(page.Rows, page.ColumnTypes), page.Page, page.PageSize, page.TotalCount, page.HasMore });
            }));

        app.MapPost("/api/db/table", (TableRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
            {
                var table = await session.Introspector.IntrospectTableAsync(new TableRef(request.Table, Blank(request.Schema)));
                var ddl = SchemaDdlRenderer.RenderTable(session.Provider, table);
                using var document = JsonDocument.Parse(
                    SchemaSerializer.ToJson(new DatabaseSchema { Name = table.Schema ?? "schema", Tables = [table] }));
                return Results.Json(new { structure = document.RootElement.GetProperty("tables")[0].Clone(), ddl });
            }));

        app.MapPost("/api/db/query", (QueryRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
            {
                var result = await session.Query.ExecuteAsync(request.Sql, request.MaxRows ?? 1000);
                return Results.Json(new
                {
                    result.IsQuery,
                    result.Columns,
                    result.ColumnTypes,
                    rows = CellFormatter.Rows(result.Rows, result.ColumnTypes),
                    result.RowsAffected,
                    result.Truncated,
                    result.ElapsedMilliseconds,
                });
            }));

        app.MapPost("/api/db/row/insert", (RowRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
                Results.Json(new { affected = await session.Writer.InsertAsync(new TableRef(request.Table, Blank(request.Schema)), ToClrMap(request.Values)) })));

        app.MapPost("/api/db/row/update", (RowRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
                Results.Json(new { affected = await session.Writer.UpdateAsync(new TableRef(request.Table, Blank(request.Schema)), ToClrMap(request.Key), ToClrMap(request.Values)) })));

        app.MapPost("/api/db/row/delete", (RowRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
                Results.Json(new { affected = await session.Writer.DeleteAsync(new TableRef(request.Table, Blank(request.Schema)), ToClrMap(request.Key)) })));

        app.MapPost("/api/db/export", (ExportRequest request, ServerRegistry registry) =>
            WithSession(request, registry, async session =>
            {
                var tableRef = new TableRef(request.Table, Blank(request.Schema));
                var page = await session.Data.QueryAsync(tableRef, ToQuery(request.Query));
                var (text, contentType) = request.Format?.ToLowerInvariant() switch
                {
                    "json" => (DataExporter.ToJson(page.Columns, page.Rows), "application/json"),
                    "sql" => (DataExporter.ToSqlInserts(session.Provider, tableRef, page.Columns, page.Rows), "application/sql"),
                    _ => (DataExporter.ToCsv(page.Columns, page.Rows), "text/csv"),
                };
                return Results.Text(text, contentType, System.Text.Encoding.UTF8);
            }));
    }

    // ------------------------------------------------------------- helpers

    private static async Task<IResult> WithSession(
        TargetRequest target,
        ServerRegistry registry,
        Func<DatabaseSession, Task<IResult>> run)
    {
        try
        {
            var (provider, connectionString) = registry.Resolve(target);
            await using var session = await DatabaseSession.OpenAsync(provider, connectionString);
            return await run(session);
        }
        catch (IntrospectionException ex)
        {
            return Fail(ex);
        }
    }

    private static IResult Fail(IntrospectionException ex) =>
        ObjectExplorer.IsAuthenticationError(ex)
            ? Results.Json(new { error = ex.Message, needsPassword = true }, statusCode: StatusCodes.Status401Unauthorized)
            : Results.BadRequest(new { error = ex.Message });

    private static bool TryBuildProfile(ServerRequest request, ServerRegistry registry, out ServerProfile profile, out string? error)
    {
        profile = null!;
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            error = "A server name is required.";
            return false;
        }

        if (!Enum.TryParse<DatabaseProvider>(request.Provider, ignoreCase: true, out var provider))
        {
            error = $"Unknown provider '{request.Provider}'.";
            return false;
        }

        if (provider == DatabaseProvider.Sqlite ? string.IsNullOrWhiteSpace(request.FilePath) : string.IsNullOrWhiteSpace(request.Host))
        {
            error = provider == DatabaseProvider.Sqlite ? "A database file path is required." : "A host is required.";
            return false;
        }

        // An edit that leaves the password blank keeps the saved one.
        var existing = string.IsNullOrEmpty(request.Id) ? null : registry.Store.Get(request.Id);
        profile = new ServerProfile
        {
            Id = string.IsNullOrEmpty(request.Id) ? Guid.NewGuid().ToString("N") : request.Id,
            Name = request.Name.Trim(),
            Group = string.IsNullOrWhiteSpace(request.Group) ? "Servers" : request.Group.Trim(),
            Provider = provider,
            Host = Blank(request.Host),
            Port = request.Port,
            MaintenanceDatabase = Blank(request.MaintenanceDatabase),
            Username = Blank(request.Username),
            Password = string.IsNullOrEmpty(request.Password) ? existing?.Password : request.Password,
            SavePassword = request.SavePassword,
            FilePath = Blank(request.FilePath),
            Options = Blank(request.Options),
            Comment = Blank(request.Comment),
        };
        error = null;
        return true;
    }

    private static object ToDto(ServerProfile s, ServerRegistry registry) => new
    {
        s.Id,
        s.Name,
        s.Group,
        provider = s.Provider.ToString(),
        s.Host,
        s.Port,
        s.MaintenanceDatabase,
        s.Username,
        s.SavePassword,
        hasPassword = !string.IsNullOrEmpty(s.Password),
        s.FilePath,
        s.Options,
        s.Comment,
        s.Endpoint,
        connected = registry.IsConnected(s.Id),
    };

    private static bool SameEndpoint(ServerProfile a, ServerProfile b) =>
        string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    private static DataQuery ToQuery(DataQueryDto? dto)
    {
        if (dto is null)
        {
            return new DataQuery();
        }

        return new DataQuery
        {
            Page = dto.Page ?? 1,
            PageSize = dto.PageSize ?? DataQuery.DefaultPageSize,
            IncludeTotalCount = dto.IncludeTotalCount ?? false,
            Filters = (dto.Filters ?? [])
                .Select(f => new FilterCondition
                {
                    Column = f.Column,
                    Operator = Enum.TryParse<FilterOperator>(f.Operator, ignoreCase: true, out var op) ? op : FilterOperator.Equals,
                    Value = f.Value is { } v ? ToClr(v) : null,
                    Values = (f.Values ?? []).Select(ToClr).ToList(),
                })
                .ToList(),
            Sort = (dto.Sort ?? [])
                .Select(s => new SortSpec(
                    s.Column,
                    string.Equals(s.Direction, "desc", StringComparison.OrdinalIgnoreCase) ? SortDirection.Descending : SortDirection.Ascending))
                .ToList(),
        };
    }

    private static IReadOnlyDictionary<string, object?> ToClrMap(Dictionary<string, JsonElement>? map) =>
        map is null ? new Dictionary<string, object?>() : map.ToDictionary(kv => kv.Key, kv => ToClr(kv.Value));

    // Collapse a JSON value to the CLR primitive the data layer will bind.
    private static object? ToClr(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        _ => element.GetRawText(),
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// The connection target of a request: either a registered server
/// (<see cref="ServerId"/>) and one of its databases, or a raw
/// <see cref="Provider"/> and <see cref="ConnectionString"/>.
/// </summary>
public class TargetRequest
{
    /// <summary>Gets or sets the registered server id.</summary>
    public string? ServerId { get; set; }

    /// <summary>Gets or sets the database on the registered server; the maintenance database when omitted.</summary>
    public string? Database { get; set; }

    /// <summary>Gets or sets the provider name, for a raw-connection-string target.</summary>
    public string? Provider { get; set; }

    /// <summary>Gets or sets the raw connection string.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Gets or sets an optional schema scope.</summary>
    public string? Schema { get; set; }
}

/// <summary>A request that targets one relation.</summary>
public class TableRequest : TargetRequest
{
    /// <summary>Gets or sets the relation name.</summary>
    public string Table { get; set; } = string.Empty;
}

/// <summary>A request to read a page of data from one relation.</summary>
public sealed class DataRequest : TableRequest
{
    /// <summary>Gets or sets the paging, filter, and sort options.</summary>
    public DataQueryDto? Query { get; set; }
}

/// <summary>A request to export the result of a read.</summary>
public sealed class ExportRequest : TableRequest
{
    /// <summary>Gets or sets the format: "csv" (default), "json", or "sql".</summary>
    public string? Format { get; set; }

    /// <summary>Gets or sets the paging, filter, and sort options.</summary>
    public DataQueryDto? Query { get; set; }
}

/// <summary>A request to insert, update, or delete one row.</summary>
public sealed class RowRequest : TableRequest
{
    /// <summary>Gets or sets the column values to write (insert/update).</summary>
    public Dictionary<string, JsonElement>? Values { get; set; }

    /// <summary>Gets or sets the key columns identifying the row (update/delete).</summary>
    public Dictionary<string, JsonElement>? Key { get; set; }
}

/// <summary>A request to run ad-hoc SQL.</summary>
public sealed class QueryRequest : TargetRequest
{
    /// <summary>Gets or sets the statement to run.</summary>
    public string Sql { get; set; } = string.Empty;

    /// <summary>Gets or sets the result-set row cap.</summary>
    public int? MaxRows { get; set; }
}

/// <summary>A request about one tree node of a registered server.</summary>
public sealed class TreeRequest
{
    /// <summary>Gets or sets the registered server id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the node.</summary>
    public NodeRef Node { get; set; } = new() { Kind = NodeKind.Server };
}

/// <summary>A server registration form (add or edit).</summary>
public sealed class ServerRequest
{
    /// <summary>Gets or sets the id of the registration being edited; empty to add.</summary>
    public string? Id { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the server group.</summary>
    public string? Group { get; set; }

    /// <summary>Gets or sets the provider name.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Gets or sets the host.</summary>
    public string? Host { get; set; }

    /// <summary>Gets or sets the port.</summary>
    public int? Port { get; set; }

    /// <summary>Gets or sets the maintenance database.</summary>
    public string? MaintenanceDatabase { get; set; }

    /// <summary>Gets or sets the login name.</summary>
    public string? Username { get; set; }

    /// <summary>Gets or sets the password; blank on an edit keeps the saved one.</summary>
    public string? Password { get; set; }

    /// <summary>Gets or sets whether to persist the password.</summary>
    public bool SavePassword { get; set; }

    /// <summary>Gets or sets the SQLite file path.</summary>
    public string? FilePath { get; set; }

    /// <summary>Gets or sets extra driver parameters.</summary>
    public string? Options { get; set; }

    /// <summary>Gets or sets a comment.</summary>
    public string? Comment { get; set; }
}

/// <summary>A connect request, carrying a password typed at the prompt.</summary>
public sealed class ConnectRequest
{
    /// <summary>Gets or sets the password; blank to use the saved one.</summary>
    public string? Password { get; set; }

    /// <summary>Gets or sets whether to persist the typed password.</summary>
    public bool SavePassword { get; set; }
}

/// <summary>The wire form of a data query.</summary>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Rows per page.</param>
/// <param name="Filters">Column filters combined with AND.</param>
/// <param name="Sort">Ordering terms.</param>
/// <param name="IncludeTotalCount">Whether to also return the matching row count.</param>
public record DataQueryDto(
    int? Page = null,
    int? PageSize = null,
    FilterDto[]? Filters = null,
    SortDto[]? Sort = null,
    bool? IncludeTotalCount = null);

/// <summary>The wire form of one filter condition.</summary>
/// <param name="Column">The column to constrain.</param>
/// <param name="Operator">The operator name (Equals, Contains, In, ...).</param>
/// <param name="Value">The single comparison value, if the operator takes one.</param>
/// <param name="Values">The candidate values for an <c>In</c> filter.</param>
public record FilterDto(string Column, string Operator, JsonElement? Value = null, JsonElement[]? Values = null);

/// <summary>The wire form of one sort term.</summary>
/// <param name="Column">The column to order by.</param>
/// <param name="Direction">"asc" or "desc"; defaults to ascending.</param>
public record SortDto(string Column, string? Direction = null);
