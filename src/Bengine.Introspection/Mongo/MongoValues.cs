namespace Bengine.Introspection.Mongo;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;

/// <summary>
/// A BSON value the grid has no CLR type for — an <c>ObjectId</c>, an embedded
/// document, an array, binary data — kept as BSON so it round-trips unchanged
/// when it is used as a key or written back, and shown in shell syntax.
/// </summary>
/// <param name="Value">The BSON value.</param>
public sealed record BsonCell(BsonValue Value)
{
    /// <inheritdoc />
    public override string ToString() => MongoValues.Shell(Value);
}

/// <summary>
/// Converts between BSON and the values the data views work with: cells read
/// from documents, text typed into the grid, and the shell syntax
/// (<c>{name: "x", _id: ObjectId("…")}</c>) used for display, previews, and
/// the console.
/// </summary>
public static partial class MongoValues
{
    private static readonly JsonWriterSettings ShellJson = new() { OutputMode = JsonOutputMode.Shell };

    /// <summary>Turns a BSON value into a grid cell: a CLR value for strings, numbers, booleans, and dates, else a <see cref="BsonCell"/>.</summary>
    /// <param name="value">The BSON value.</param>
    /// <returns>The cell value; <c>null</c> for BSON null and undefined.</returns>
    public static object? ToCell(BsonValue? value) => value?.BsonType switch
    {
        null or BsonType.Null or BsonType.Undefined => null,
        BsonType.String => value.AsString,
        BsonType.Int32 => value.AsInt32,
        BsonType.Int64 => value.AsInt64,
        BsonType.Double => value.AsDouble,
        BsonType.Boolean => value.AsBoolean,
        BsonType.DateTime when value.AsBsonDateTime.IsValidDateTime => value.ToUniversalTime(),
        BsonType.Decimal128 when Decimal(value.AsDecimal128) is { } number => number,
        _ => new BsonCell(value),
    };

    /// <summary>Turns a cell or parameter value into BSON.</summary>
    /// <param name="value">The value: a <see cref="BsonCell"/>, a <see cref="BsonValue"/>, or a CLR value.</param>
    /// <returns>The BSON value.</returns>
    public static BsonValue ToBson(object? value) => value switch
    {
        null or DBNull => BsonNull.Value,
        BsonCell cell => cell.Value,
        BsonValue bson => bson,
        string text => new BsonString(text),
        bool flag => flag ? BsonBoolean.True : BsonBoolean.False,
        int or short or byte or sbyte or ushort => new BsonInt32(Convert.ToInt32(value, CultureInfo.InvariantCulture)),
        long or uint => new BsonInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        ulong big => big <= long.MaxValue ? new BsonInt64((long)big) : new BsonDecimal128(big),
        double number => new BsonDouble(number),
        float number => new BsonDouble(number),
        decimal number => new BsonDecimal128(number),
        DateTime time => new BsonDateTime(time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time),
        DateTimeOffset time => new BsonDateTime(time.UtcDateTime),
        DateOnly date => new BsonDateTime(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
        Guid guid => new BsonBinaryData(guid, GuidRepresentation.Standard),
        byte[] bytes => new BsonBinaryData(bytes),
        _ => new BsonString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>Gets the shell's name for a value's type (<c>string</c>, <c>int</c>, <c>objectId</c>, <c>object</c>, …).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The type name.</returns>
    public static string TypeName(BsonValue value) => value.BsonType switch
    {
        BsonType.Document => "object",
        BsonType.Array => "array",
        BsonType.String => "string",
        BsonType.Int32 => "int",
        BsonType.Int64 => "long",
        BsonType.Double => "double",
        BsonType.Decimal128 => "decimal",
        BsonType.Boolean => "bool",
        BsonType.DateTime => "date",
        BsonType.ObjectId => "objectId",
        BsonType.Binary when value.AsBsonBinaryData.SubType == BsonBinarySubType.UuidStandard => "uuid",
        BsonType.Binary => "binData",
        BsonType.Null => "null",
        BsonType.Undefined => "undefined",
        BsonType.RegularExpression => "regex",
        BsonType.Timestamp => "timestamp",
        BsonType.JavaScript or BsonType.JavaScriptWithScope => "javascript",
        BsonType.Symbol => "symbol",
        BsonType.MinKey => "minKey",
        BsonType.MaxKey => "maxKey",
        _ => value.BsonType.ToString(),
    };

    /// <summary>
    /// Parses text typed into a grid cell as a value of the column's type: a
    /// number for a numeric column, a date for a date column, an
    /// <c>ObjectId</c> for 24 hex digits in an <c>objectId</c> column, and shell
    /// syntax for documents and arrays. Text that does not fit the type, and
    /// any text in a <c>string</c> column, stays a string.
    /// </summary>
    /// <param name="text">The typed text.</param>
    /// <param name="type">The column's type name (as <see cref="TypeName"/> gives it), or <c>null</c> when unknown.</param>
    /// <returns>The value.</returns>
    public static BsonValue FromText(string text, string? type)
    {
        var trimmed = text.Trim();
        switch (type)
        {
            case "string" or "symbol":
                return new BsonString(text);
            case "int" when int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var small):
                return new BsonInt32(small);
            case "int" or "long" when long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var big):
                return new BsonInt64(big);
            case "double" when double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var real):
                return new BsonDouble(real);
            case "decimal" when Decimal128.TryParse(trimmed, out var exact):
                return new BsonDecimal128(exact);
            case "bool" when bool.TryParse(trimmed, out var flag):
                return flag ? BsonBoolean.True : BsonBoolean.False;
            case "date" when DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time):
                return new BsonDateTime(time);
            case "objectId" when ObjectId.TryParse(trimmed, out var id):
                return new BsonObjectId(id);
            case "uuid" when Guid.TryParse(trimmed, out var guid):
                return new BsonBinaryData(guid, GuidRepresentation.Standard);
        }

        // Anything else is shell syntax when it reads as a value, else a string.
        if (LooksLikeValue().IsMatch(trimmed) && TryParseValue(trimmed, out var parsed))
        {
            return parsed;
        }

        return new BsonString(text);
    }

    /// <summary>Parses one value in shell syntax (<c>{a: 1}</c>, <c>[1, 2]</c>, <c>ObjectId("…")</c>, <c>ISODate("…")</c>, <c>/re/i</c>, …).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    /// <exception cref="IntrospectionException">The text is not one value.</exception>
    public static BsonValue ParseValue(string text)
    {
        try
        {
            using var reader = new JsonReader(text);
            var value = BsonSerializer.Deserialize<BsonValue>(reader);
            if (!reader.IsAtEndOfFile())
            {
                throw new FormatException("unexpected text after the value");
            }

            return value;
        }
        catch (Exception ex) when (ex is FormatException or BsonException or InvalidOperationException or ArgumentException or OverflowException)
        {
            var shown = text.Length > 60 ? text[..60] + "…" : text;
            throw new IntrospectionException($"Could not read '{shown.ReplaceLineEndings(" ")}': {ex.Message}", ex);
        }
    }

    /// <summary>Tries to parse one value in shell syntax.</summary>
    /// <param name="text">The text.</param>
    /// <param name="value">The value, when it parsed.</param>
    /// <returns><c>true</c> if the text is one value.</returns>
    public static bool TryParseValue(string text, out BsonValue value)
    {
        try
        {
            value = ParseValue(text);
            return true;
        }
        catch (IntrospectionException)
        {
            value = BsonNull.Value;
            return false;
        }
    }

    /// <summary>Renders a value in compact shell syntax that <see cref="ParseValue"/> reads back.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The text.</returns>
    public static string Shell(BsonValue value)
    {
        var text = new StringBuilder();
        Write(text, value);
        return text.ToString();
    }

    /// <summary>Quotes a string as a shell string literal.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The literal, in double quotes.</returns>
    public static string Quote(string text)
    {
        var quoted = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>Renders a field name as the shell writes it: bare when it is an identifier, else quoted.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The key text.</returns>
    public static string Key(string name) => Identifier().IsMatch(name) ? name : Quote(name);

    private static void Write(StringBuilder text, BsonValue value)
    {
        switch (value.BsonType)
        {
            case BsonType.Document:
                var document = value.AsBsonDocument;
                if (document.ElementCount == 0)
                {
                    text.Append("{}");
                    return;
                }

                text.Append('{');
                var first = true;
                foreach (var element in document)
                {
                    text.Append(first ? string.Empty : ", ").Append(Key(element.Name)).Append(": ");
                    Write(text, element.Value);
                    first = false;
                }

                text.Append('}');
                return;
            case BsonType.Array:
                text.Append('[');
                var index = 0;
                foreach (var item in value.AsBsonArray)
                {
                    text.Append(index++ == 0 ? string.Empty : ", ");
                    Write(text, item);
                }

                text.Append(']');
                return;
            case BsonType.String:
                text.Append(Quote(value.AsString));
                return;
            case BsonType.Int32:
                text.Append(value.AsInt32.ToString(CultureInfo.InvariantCulture));
                return;
            case BsonType.Double:
                var number = value.AsDouble;
                var written = number.ToString("R", CultureInfo.InvariantCulture);

                // Keep a whole double a double when it is read back ("5" would be an int).
                text.Append(double.IsFinite(number) && !written.Contains('.') && !written.Contains('E') ? written + ".0" : double.IsFinite(number) ? written : value.ToJson(ShellJson));
                return;
            case BsonType.Boolean:
                text.Append(value.AsBoolean ? "true" : "false");
                return;
            case BsonType.Null:
                text.Append("null");
                return;
            case BsonType.ObjectId:
                text.Append("ObjectId(\"").Append(value.AsObjectId.ToString()).Append("\")");
                return;
            case BsonType.DateTime when value.AsBsonDateTime.IsValidDateTime:
                text.Append("ISODate(\"").Append(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFF'Z'", CultureInfo.InvariantCulture)).Append("\")");
                return;
            default:
                text.Append(value.ToJson(ShellJson));
                return;
        }
    }

    private static decimal? Decimal(Decimal128 value)
    {
        try
        {
            return Decimal128.IsNaN(value) || Decimal128.IsInfinity(value) ? null : Decimal128.ToDecimal(value);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z_$][A-Za-z0-9_$]*$")]
    private static partial Regex Identifier();

    // Text that reads as a shell value: a document, an array, a number, a keyword, or a constructor.
    [GeneratedRegex(@"^(?:[\[{/]|-?\d|-?\.\d|true$|false$|null$|(?:new\s+)?(?:ObjectId|ISODate|Date|NumberLong|NumberInt|NumberDecimal|UUID|BinData|HexData|Timestamp|MinKey|MaxKey)\b)")]
    private static partial Regex LooksLikeValue();
}
