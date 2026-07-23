namespace Bengine.Enums;

/// <summary>
/// Hint describing how a column's value should be physically stored, where the
/// provider supports such control.
/// </summary>
public enum StorageStrategy
{
    /// <summary>Use the provider default storage strategy.</summary>
    Default,

    /// <summary>Store inline in the main row, uncompressed.</summary>
    Plain,

    /// <summary>Store inline, allowing compression.</summary>
    Main,

    /// <summary>Allow out-of-line (TOAST) storage without compression.</summary>
    External,

    /// <summary>Allow out-of-line storage with compression.</summary>
    Extended,
}
