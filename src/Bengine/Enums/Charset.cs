namespace Bengine.Enums;

/// <summary>
/// Character set applied to a textual column or table.
/// </summary>
public enum Charset
{
    /// <summary>Use the provider or database default character set.</summary>
    Default,

    /// <summary>UTF-8 encoding.</summary>
    Utf8,

    /// <summary>UTF-8 encoding with full four-byte support (MySQL <c>utf8mb4</c>).</summary>
    Utf8Mb4,

    /// <summary>UTF-16 encoding.</summary>
    Utf16,

    /// <summary>Latin-1 (ISO-8859-1) encoding.</summary>
    Latin1,

    /// <summary>ASCII encoding.</summary>
    Ascii,
}
