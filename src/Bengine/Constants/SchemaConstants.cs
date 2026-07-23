namespace Bengine.Constants;

using System.Collections.Frozen;
using Bengine.Enums;

/// <summary>
/// Shared, immutable constant sets reused across metadata, mapping, validation,
/// and generation. Centralizing them avoids duplicating provider and language
/// enumerations throughout the framework.
/// </summary>
public static class SchemaConstants
{
    /// <summary>Gets the set of every supported <see cref="DatabaseProvider"/>.</summary>
    public static FrozenSet<DatabaseProvider> AllProviders { get; } =
        Enum.GetValues<DatabaseProvider>().ToFrozenSet();

    /// <summary>Gets the set of every supported <see cref="Language"/>.</summary>
    public static FrozenSet<Language> AllLanguages { get; } =
        Enum.GetValues<Language>().ToFrozenSet();

    /// <summary>The default schema name used when a schema-qualified object omits one.</summary>
    public const string DefaultSchema = "public";
}
