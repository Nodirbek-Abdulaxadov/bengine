namespace Bengine.Generators.Sql;

using Bengine.Mapping;

/// <summary>Generates SQLite DDL from a schema AST.</summary>
public sealed class SqliteGenerator : SqlGeneratorBase
{
    /// <summary>Initializes the generator with the shared type mapper.</summary>
    public SqliteGenerator()
        : base(new SqliteDialect(), TypeMapper.Instance)
    {
    }

    /// <summary>Initializes the generator with a specific type mapper.</summary>
    /// <param name="mapper">The type mapper to use.</param>
    public SqliteGenerator(ITypeMapper mapper)
        : base(new SqliteDialect(), mapper)
    {
    }
}
