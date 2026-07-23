namespace Bengine.Parsing;

/// <summary>
/// A single lexical token together with its position in the original SQL text.
/// The <see cref="Start"/> and <see cref="End"/> offsets let the parser slice the
/// source verbatim to preserve expressions (check constraints, generated columns,
/// defaults) exactly as written.
/// </summary>
/// <param name="Kind">The lexical category of the token.</param>
/// <param name="Text">The token's value: the undelimited name for <see cref="SqlTokenKind.Quoted"/>, otherwise the raw lexeme.</param>
/// <param name="Start">The inclusive start offset of the token in the source.</param>
/// <param name="End">The exclusive end offset of the token in the source.</param>
/// <param name="Quote">The opening delimiter for a quoted identifier (<c>"</c>, <c>`</c>, or <c>[</c>), or <c>'\0'</c> when the token is not delimited.</param>
public readonly record struct SqlToken(
    SqlTokenKind Kind,
    string Text,
    int Start,
    int End,
    char Quote)
{
    /// <summary>Determines whether the token is an unquoted word equal to <paramref name="keyword"/>, ignoring case.</summary>
    /// <param name="keyword">The keyword to compare against.</param>
    /// <returns><c>true</c> if the token is a matching unquoted word; otherwise <c>false</c>.</returns>
    public bool IsKeyword(string keyword) =>
        Kind == SqlTokenKind.Word && string.Equals(Text, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Determines whether the token is the given single punctuation character.</summary>
    /// <param name="symbol">The punctuation character to compare against.</param>
    /// <returns><c>true</c> if the token is that punctuation; otherwise <c>false</c>.</returns>
    public bool IsPunct(char symbol) =>
        Kind == SqlTokenKind.Punct && Text.Length == 1 && Text[0] == symbol;
}
