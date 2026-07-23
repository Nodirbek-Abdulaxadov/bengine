namespace Bengine.Parsing;

/// <summary>
/// Raised when SQL DDL cannot be parsed into a <see cref="Bengine.Models.DatabaseSchema"/>.
/// The message is safe to surface to a user and names the offending construct.
/// </summary>
public sealed class SqlParseException : Exception
{
    /// <summary>Initializes a new instance with a human-readable message.</summary>
    /// <param name="message">A description of the failure.</param>
    public SqlParseException(string message)
        : base(message)
    {
    }
}
