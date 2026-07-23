namespace Bengine.Parsing;

/// <summary>
/// The lexical category of a single <see cref="SqlToken"/> produced by the
/// <see cref="SqlTokenizer"/>. The tokenizer is deliberately provider-neutral:
/// keyword recognition is left to the parser, which compares the text of
/// <see cref="Word"/> tokens case-insensitively.
/// </summary>
public enum SqlTokenKind
{
    /// <summary>An unquoted identifier or keyword (for example <c>varchar</c> or <c>CREATE</c>).</summary>
    Word,

    /// <summary>A delimited identifier such as <c>"col"</c>, <c>`col`</c>, or <c>[col]</c>; its text is the undelimited name.</summary>
    Quoted,

    /// <summary>A single-quoted string literal; its text retains the surrounding quotes.</summary>
    String,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>A single punctuation or operator character such as <c>(</c>, <c>,</c>, or <c>;</c>.</summary>
    Punct,
}
