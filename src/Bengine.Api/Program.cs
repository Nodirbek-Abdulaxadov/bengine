using System.Text.Json;
using Bengine.Enums;
using Bengine.Generators;
using Bengine.Generators.Languages;
using Bengine.Generators.Migrations;
using Bengine.Generators.Sql;
using Bengine.Models;
using Bengine.Parsing;
using Bengine.Serialization;
using Bengine.Validation;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5099");

// Serialize enums by name so responses carry "Error"/"Warning" and provider /
// language names rather than opaque numbers.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter()));

const string DevCors = "designer";
builder.Services.AddCors(options =>
    options.AddPolicy(DevCors, policy =>
        policy.WithOrigins("http://localhost:5173", "http://localhost:4173")
            .AllowAnyHeader()
            .AllowAnyMethod()));

var app = builder.Build();
app.UseCors(DevCors);

// --- Metadata endpoints ---
app.MapGet("/api/providers", () => Enum.GetNames<DatabaseProvider>());
app.MapGet("/api/languages", () => Enum.GetNames<Language>());

// --- Generation endpoints. The request body is the schema.json document. ---
app.MapPost("/api/generate/sql/{provider}", async (string provider, HttpRequest request) =>
{
    if (!Enum.TryParse<DatabaseProvider>(provider, ignoreCase: true, out var target))
    {
        return Results.BadRequest(new { error = $"Unknown provider '{provider}'." });
    }

    return await RunAsync(request, schema =>
    {
        var validation = new DatabaseValidator().Validate(schema, target);
        var sql = SqlGeneratorFor(target).Generate(schema);
        return Results.Json(new { provider = target.ToString(), valid = validation.IsValid, validation.Messages, sql });
    });
});

app.MapPost("/api/generate/code/{language}", async (string language, HttpRequest request) =>
{
    if (!Enum.TryParse<Language>(language, ignoreCase: true, out var target))
    {
        return Results.BadRequest(new { error = $"Unknown language '{language}'." });
    }

    return await RunAsync(request, schema =>
        Results.Json(new { language = target.ToString(), code = CodeGeneratorFor(target).Generate(schema) }));
});

app.MapPost("/api/generate/migration/{provider}", async (string provider, HttpRequest request) =>
{
    if (!Enum.TryParse<DatabaseProvider>(provider, ignoreCase: true, out var target))
    {
        return Results.BadRequest(new { error = $"Unknown provider '{provider}'." });
    }

    return await RunAsync(request, schema =>
    {
        var migration = new MigrationGenerator(SqlGeneratorFor(target)).CreateInitial(schema);
        return Results.Json(new { migration.Name, migration.Up, migration.Down });
    });
});

app.MapPost("/api/validate/{provider}", async (string provider, HttpRequest request) =>
{
    if (!Enum.TryParse<DatabaseProvider>(provider, ignoreCase: true, out var target))
    {
        return Results.BadRequest(new { error = $"Unknown provider '{provider}'." });
    }

    return await RunAsync(request, schema =>
    {
        var validation = new DatabaseValidator().Validate(schema, target);
        return Results.Json(new { valid = validation.IsValid, validation.Messages });
    });
});

// --- Import endpoint. Parses SQL DDL into a schema.json document. ---
app.MapPost("/api/import/sql", (ImportSqlRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Sql))
    {
        return Results.BadRequest(new { error = "Request body must contain SQL in the 'sql' field." });
    }

    DatabaseProvider? explicitProvider = null;
    if (!string.IsNullOrWhiteSpace(request.Dialect)
        && !request.Dialect.Equals("auto", StringComparison.OrdinalIgnoreCase))
    {
        if (!Enum.TryParse<DatabaseProvider>(request.Dialect, ignoreCase: true, out var parsed))
        {
            return Results.BadRequest(new { error = $"Unknown dialect '{request.Dialect}'." });
        }

        explicitProvider = parsed;
    }

    SqlImportResult result;
    try
    {
        result = explicitProvider is { } provider
            ? SqlSchemaParser.Import(request.Sql, provider)
            : SqlSchemaParser.Import(request.Sql);
    }
    catch (SqlParseException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    // Serialize through the schema serializer so the document matches the exact
    // schema.json contract the designer consumes, then embed it verbatim.
    using var document = JsonDocument.Parse(SchemaSerializer.ToJson(result.Schema));
    return Results.Json(new
    {
        dialect = result.Provider.ToString(),
        detected = result.Detected,
        tableCount = result.Schema.Tables.Count,
        schema = document.RootElement.Clone(),
    });
});

app.Run();
return;

// --- helpers ---
static async Task<IResult> RunAsync(HttpRequest request, Func<DatabaseSchema, IResult> generate)
{
    string body;
    using (var reader = new StreamReader(request.Body))
    {
        body = await reader.ReadToEndAsync();
    }

    if (string.IsNullOrWhiteSpace(body))
    {
        return Results.BadRequest(new { error = "Request body must contain a schema.json document." });
    }

    DatabaseSchema schema;
    try
    {
        schema = SchemaSerializer.FromJson(body);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Invalid schema.json: {ex.Message}" });
    }

    return generate(schema);
}

static ISqlGenerator SqlGeneratorFor(DatabaseProvider provider) => provider switch
{
    DatabaseProvider.PostgreSql => new PostgreSqlGenerator(),
    DatabaseProvider.SqlServer => new SqlServerGenerator(),
    DatabaseProvider.MySql => new MySqlGenerator(),
    DatabaseProvider.Sqlite => new SqliteGenerator(),
    _ => throw new ArgumentOutOfRangeException(nameof(provider)),
};

static ICodeGenerator CodeGeneratorFor(Language language) => language switch
{
    Language.CSharp => new CSharpGenerator(),
    Language.Go => new GoGenerator(),
    Language.Rust => new RustGenerator(),
    Language.TypeScript => new TypeScriptGenerator(),
    Language.Python => new PythonGenerator(),
    Language.Php => new PhpGenerator(),
    _ => throw new ArgumentOutOfRangeException(nameof(language)),
};

/// <summary>The body of a SQL import request: the SQL text and an optional dialect override ("auto" or empty to auto-detect).</summary>
/// <param name="Sql">The SQL DDL to parse.</param>
/// <param name="Dialect">An optional provider name to force the dialect; auto-detected when omitted.</param>
record ImportSqlRequest(string? Sql, string? Dialect);
