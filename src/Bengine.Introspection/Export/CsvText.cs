namespace Bengine.Introspection.Export;

using System.Text;

/// <summary>
/// Parses delimiter-separated text — clipboard blocks copied from a grid or a
/// spreadsheet, and CSV/TSV files — honoring RFC 4180 quoting (quoted fields
/// may contain the delimiter, doubled quotes, and line breaks).
/// </summary>
public static class CsvText
{
    /// <summary>Guesses the delimiter of a text from its first line: tab, semicolon, pipe, or comma.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The delimiter.</returns>
    public static char DetectDelimiter(string text)
    {
        var firstLine = text.Split('\n', 2)[0];
        char[] candidates = ['\t', ';', '|', ','];
        return candidates.OrderByDescending(c => firstLine.Count(x => x == c)).ThenBy(c => Array.IndexOf(candidates, c))
            .FirstOrDefault(c => firstLine.Contains(c), '\t');
    }

    /// <summary>Reads delimited records one at a time from a reader (for files of any size).</summary>
    /// <param name="reader">The reader.</param>
    /// <param name="delimiter">The delimiter.</param>
    /// <returns>The records.</returns>
    public static IEnumerable<List<string>> Read(TextReader reader, char delimiter)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;
        int next;
        while ((next = reader.Read()) >= 0)
        {
            var c = (char)next;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (c == delimiter)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                }

                row.Add(field.ToString());
                if (!(row.Count == 1 && row[0].Length == 0))
                {
                    yield return row;
                }

                row = [];
                field.Clear();
                fieldStarted = false;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }

        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    /// <summary>Parses the text into rows of fields.</summary>
    /// <param name="text">The text.</param>
    /// <param name="delimiter">The delimiter, or <c>null</c> to detect it.</param>
    /// <returns>The rows; a trailing empty line is dropped.</returns>
    public static List<List<string>> Parse(string text, char? delimiter = null)
    {
        var separator = delimiter ?? DetectDelimiter(text);
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        var fieldStarted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }

                continue;
            }

            if (c == '"' && !fieldStarted)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
                fieldStarted = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                row.Add(field.ToString());
                rows.Add(row);
                row = [];
                field.Clear();
                fieldStarted = false;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }
        }

        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }

        return rows;
    }
}
