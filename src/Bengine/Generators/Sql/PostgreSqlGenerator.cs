namespace Bengine.Generators.Sql;

using Bengine.Mapping;

/// <summary>Generates PostgreSQL DDL from a schema AST.</summary>
public sealed class PostgreSqlGenerator : SqlGeneratorBase
{
    /// <summary>Initializes the generator with the shared type mapper.</summary>
    public PostgreSqlGenerator()
        : base(new PostgreSqlDialect(), TypeMapper.Instance)
    {
    }

    /// <summary>Initializes the generator with a specific type mapper.</summary>
    /// <param name="mapper">The type mapper to use.</param>
    public PostgreSqlGenerator(ITypeMapper mapper)
        : base(new PostgreSqlDialect(), mapper)
    {
    }
}
