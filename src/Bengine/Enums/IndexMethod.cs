namespace Bengine.Enums;

/// <summary>
/// Physical access method (algorithm) backing an index. Availability is
/// provider-dependent and resolved through validation.
/// </summary>
public enum IndexMethod
{
    /// <summary>Balanced-tree index; the default for most providers.</summary>
    BTree,

    /// <summary>Hash index for equality lookups.</summary>
    Hash,

    /// <summary>Generalized inverted index (PostgreSQL GIN).</summary>
    Gin,

    /// <summary>Generalized search tree (PostgreSQL GiST).</summary>
    Gist,

    /// <summary>Block range index (PostgreSQL BRIN).</summary>
    Brin,

    /// <summary>Space-partitioned GiST (PostgreSQL SP-GiST).</summary>
    SpGist,
}
