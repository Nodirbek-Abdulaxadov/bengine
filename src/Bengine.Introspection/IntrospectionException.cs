namespace Bengine.Introspection;

/// <summary>
/// Thrown when a live database cannot be connected to, discovered, introspected,
/// or read. Wraps the underlying ADO.NET failure (available as
/// <see cref="System.Exception.InnerException"/>) behind a provider-independent
/// type so callers need not reference a specific driver to catch it.
/// </summary>
public sealed class IntrospectionException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">A description of what failed.</param>
    public IntrospectionException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    /// <param name="message">A description of what failed.</param>
    /// <param name="innerException">The underlying driver or I/O failure.</param>
    public IntrospectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Gets the 1-based character offset in the statement where the server
    /// located the error, when it reports one (PostgreSQL syntax and name errors).
    /// </summary>
    public int? Position { get; init; }
}
