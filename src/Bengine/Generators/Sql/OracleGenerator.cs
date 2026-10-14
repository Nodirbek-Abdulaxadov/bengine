namespace Bengine.Generators.Sql;

using Bengine.Mapping;

/// <summary>Generates Oracle DDL from the schema AST.</summary>
public sealed class OracleGenerator : SqlGeneratorBase
{
    /// <summary>Initializes the generator with the default type mapper.</summary>
    /// <param name="foldNames">Whether plain names are written bare (designs) or kept exactly (live databases); see <see cref="OracleDialect"/>.</param>
    public OracleGenerator(bool foldNames = true)
        : base(new OracleDialect(foldNames), TypeMapper.Instance)
    {
    }

    /// <summary>Initializes the generator with a custom type mapper.</summary>
    /// <param name="mapper">The type mapper.</param>
    public OracleGenerator(ITypeMapper mapper)
        : base(new OracleDialect(), mapper)
    {
    }
}
