namespace Bengine.Generators.Sql;

using Bengine.Mapping;

/// <summary>Generates MySQL DDL from a schema AST.</summary>
public sealed class MySqlGenerator : SqlGeneratorBase
{
    /// <summary>Initializes the generator with the shared type mapper.</summary>
    public MySqlGenerator()
        : base(new MySqlDialect(), TypeMapper.Instance)
    {
    }

    /// <summary>Initializes the generator with a specific type mapper.</summary>
    /// <param name="mapper">The type mapper to use.</param>
    public MySqlGenerator(ITypeMapper mapper)
        : base(new MySqlDialect(), mapper)
    {
    }
}
