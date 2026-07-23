namespace Bengine.Enums;

/// <summary>
/// Logical role of an index, independent of the physical access method.
/// </summary>
public enum IndexType
{
    /// <summary>A non-unique secondary index.</summary>
    NonUnique,

    /// <summary>A unique index enforcing distinct key values.</summary>
    Unique,

    /// <summary>A full-text search index.</summary>
    FullText,

    /// <summary>A spatial index.</summary>
    Spatial,
}
