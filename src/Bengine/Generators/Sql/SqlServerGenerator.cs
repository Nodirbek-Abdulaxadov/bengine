namespace Bengine.Generators.Sql;

using Bengine.Mapping;

/// <summary>Generates SQL Server DDL from a schema AST.</summary>
public sealed class SqlServerGenerator : SqlGeneratorBase
{
    /// <summary>Initializes the generator with the shared type mapper.</summary>
    public SqlServerGenerator()
        : base(new SqlServerDialect(), TypeMapper.Instance)
    {
    }

    /// <summary>Initializes the generator with a specific type mapper.</summary>
    /// <param name="mapper">The type mapper to use.</param>
    public SqlServerGenerator(ITypeMapper mapper)
        : base(new SqlServerDialect(), mapper)
    {
    }
}
