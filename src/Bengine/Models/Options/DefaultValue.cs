namespace Bengine.Models.Options;

using Bengine.Enums;

/// <summary>
/// Immutable representation of a column's default value. The <see cref="Kind"/>
/// tells generators how to interpret <see cref="Value"/> without inspecting its
/// text, keeping default handling provider-independent.
/// </summary>
public sealed record DefaultValue
{
    /// <summary>Gets the classification of this default.</summary>
    public required DefaultValueKind Kind { get; init; }

    /// <summary>
    /// Gets the raw literal, expression, or function text, or <c>null</c> for
    /// kinds whose meaning is self-contained (such as
    /// <see cref="DefaultValueKind.CurrentTimestamp"/>).
    /// </summary>
    public string? Value { get; init; }

    /// <summary>Gets a shared instance representing the absence of a default.</summary>
    public static DefaultValue None { get; } = new() { Kind = DefaultValueKind.None };

    /// <summary>Creates a literal default.</summary>
    /// <param name="literal">The literal text as it should appear in SQL.</param>
    /// <returns>A literal <see cref="DefaultValue"/>.</returns>
    public static DefaultValue Literal(string literal) =>
        new() { Kind = DefaultValueKind.Literal, Value = literal };

    /// <summary>Creates an expression default.</summary>
    /// <param name="expression">The provider-evaluated expression.</param>
    /// <returns>An expression <see cref="DefaultValue"/>.</returns>
    public static DefaultValue Expression(string expression) =>
        new() { Kind = DefaultValueKind.Expression, Value = expression };

    /// <summary>Creates a function-call default.</summary>
    /// <param name="functionCall">The function invocation text.</param>
    /// <returns>A function <see cref="DefaultValue"/>.</returns>
    public static DefaultValue Function(string functionCall) =>
        new() { Kind = DefaultValueKind.Function, Value = functionCall };

    /// <summary>Gets a default equal to the current transaction timestamp.</summary>
    public static DefaultValue CurrentTimestamp { get; } =
        new() { Kind = DefaultValueKind.CurrentTimestamp };
}
