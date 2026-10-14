using Bengine.Enums;
using Bengine.Generators;
using Bengine.Generators.Languages;
using Bengine.Generators.Sql;
using Bengine.Models;
using Bengine.Models.Options;
using Bengine.Models.Options.Validation;
using Bengine.Parsing;
using Bengine.Serialization;
using Bengine.Validation;
using Index = Bengine.Models.Index;

// Consume a designer-produced schema.json — or a raw .sql DDL script — when a
// path is supplied; otherwise fall back to a small schema built inline.
DatabaseSchema schema = args is [var inputPath, ..] && File.Exists(inputPath)
    ? LoadSchema(inputPath)
    : new DatabaseSchema
{
    Name = "shop",
    Tables =
    [
        new Table
        {
            Name = "users",
            Schema = "public",
            Columns =
            [
                new Column
                {
                    Name = "id",
                    Type = ColumnTypeOptions.Of(DbDataType.BigInt),
                    Identity = new IdentityOptions { Strategy = IdentityStrategy.Identity },
                },
                new Column
                {
                    Name = "email",
                    Type = new ColumnTypeOptions { DataType = DbDataType.VarChar, Length = 320 },
                    Validation = new ColumnValidation
                    {
                        Kind = ValidationKind.String,
                        String = new StringValidation { MinLength = 3, MaxLength = 320 },
                    },
                },
                new Column
                {
                    Name = "created_at",
                    Type = ColumnTypeOptions.Of(DbDataType.TimestampTz),
                    Default = DefaultValue.CurrentTimestamp,
                },
            ],
            PrimaryKey = new PrimaryKey { Name = "pk_users", ColumnNames = ["id"] },
            Indexes =
            [
                new Index
                {
                    Name = "ux_users_email",
                    Type = IndexType.Unique,
                    Columns = [IndexColumn.Ascending("email")],
                },
            ],
        },
        new Table
        {
            Name = "orders",
            Schema = "public",
            Columns =
            [
                new Column
                {
                    Name = "id",
                    Type = ColumnTypeOptions.Of(DbDataType.BigInt),
                    Identity = new IdentityOptions { Strategy = IdentityStrategy.Identity },
                },
                new Column { Name = "user_id", Type = ColumnTypeOptions.Of(DbDataType.BigInt) },
                new Column
                {
                    Name = "total",
                    Type = new ColumnTypeOptions { DataType = DbDataType.Decimal, Precision = 12, Scale = 2 },
                },
            ],
            PrimaryKey = new PrimaryKey { Name = "pk_orders", ColumnNames = ["id"] },
            ForeignKeys =
            [
                new ForeignKey
                {
                    Name = "fk_orders_user",
                    Columns = ["user_id"],
                    ReferencedTable = "users",
                    ReferencedSchema = "public",
                    ReferencedColumns = ["id"],
                    OnDelete = ReferentialAction.Cascade,
                },
            ],
            CheckConstraints =
            [
                new CheckConstraint { Name = "ck_orders_total", Expression = "total >= 0" },
            ],
        },
    ],
};

// JSON round-trip sanity check: serialize the AST to the schema.json contract,
// rebuild it, and confirm the generated DDL is identical before and after.
var json = SchemaSerializer.ToJson(schema);
var restored = SchemaSerializer.FromJson(json);

var before = new PostgreSqlGenerator().Generate(schema);
var after = new PostgreSqlGenerator().Generate(restored);
if (before != after)
{
    throw new InvalidOperationException("JSON round-trip changed the generated DDL.");
}

Console.WriteLine("JSON round-trip: OK (DDL identical before/after)");
Console.WriteLine();

schema = restored;

// Every supported SQL provider and its generator.
ISqlGenerator[] sqlGenerators =
[
    new PostgreSqlGenerator(),
    new SqlServerGenerator(),
    new MySqlGenerator(),
    new SqliteGenerator(),
    new OracleGenerator(),
];

// Every supported target language and its generator.
ICodeGenerator[] codeGenerators =
[
    new CSharpGenerator(),
    new GoGenerator(),
    new RustGenerator(),
    new TypeScriptGenerator(),
    new PythonGenerator(),
    new PhpGenerator(),
];

var validator = new DatabaseValidator();

// 1. Validate + generate DDL for every provider.
foreach (var generator in sqlGenerators)
{
    var validation = validator.Validate(schema, generator.Provider);
    Console.WriteLine($"========== {generator.Provider} (valid: {validation.IsValid}) ==========");
    foreach (var message in validation.Messages)
    {
        Console.WriteLine($"  [{message.Severity}] {message.Path}: {message.Message}");
    }

    Console.WriteLine(generator.Generate(schema));
}

// 2. Generate models for every language.
foreach (var generator in codeGenerators)
{
    Console.WriteLine($"========== {generator.Language} ==========");
    Console.WriteLine(generator.Generate(schema));
}

return;

// Loads a schema from either a schema.json document or a raw .sql DDL script,
// choosing by file extension. For SQL, the dialect is auto-detected.
static DatabaseSchema LoadSchema(string path)
{
    if (path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
    {
        var import = SqlSchemaParser.Import(File.ReadAllText(path));
        Console.WriteLine($"Imported {import.Schema.Tables.Count} table(s) from SQL (detected dialect: {import.Provider}).");
        Console.WriteLine();
        return import.Schema;
    }

    return SchemaSerializer.Load(path);
}
