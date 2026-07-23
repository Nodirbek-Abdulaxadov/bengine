using Bengine.Enums;
using Bengine.Generators;
using Bengine.Generators.Languages;
using Bengine.Generators.Migrations;
using Bengine.Generators.Sql;
using Bengine.Models;
using Bengine.Serialization;
using Bengine.Validation;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5099");

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
