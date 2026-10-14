namespace Bengine.Introspection.Export;

using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

/// <summary>
/// Writes rows as a minimal Office Open XML workbook (<c>.xlsx</c>) with one
/// sheet: a bold, frozen header row, numbers and booleans as typed cells, and
/// everything else as inline strings. It needs no spreadsheet library — the
/// format is a ZIP of a few XML parts.
/// </summary>
public static class XlsxWriter
{
    /// <summary>Builds the workbook.</summary>
    /// <param name="sheetName">The sheet name (trimmed to Excel's 31-character limit).</param>
    /// <param name="columns">The column names.</param>
    /// <param name="rows">The rows, aligned with <paramref name="columns"/>.</param>
    /// <returns>The <c>.xlsx</c> file bytes.</returns>
    public static byte[] Write(string sheetName, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var name = new string(sheetName.Where(c => c is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray());
        name = name.Length == 0 ? "Sheet1" : name[..Math.Min(31, name.Length)];

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);
            Add(zip, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);
            Add(zip, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="{SecurityElement.Escape(name)}" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """);
            Add(zip, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);
            Add(zip, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts>
                  <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
                  <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
                  <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
                  <cellXfs count="2"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="1" fillId="0" borderId="0" xfId="0" applyFont="1"/></cellXfs>
                </styleSheet>
                """);
            Add(zip, "xl/worksheets/sheet1.xml", Sheet(columns, rows));
        }

        return buffer.ToArray();
    }

    private static string Sheet(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var xml = new StringBuilder();
        xml.Append("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?>""");
        xml.Append("""<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">""");
        xml.Append("""<sheetViews><sheetView workbookViewId="0"><pane ySplit="1" topLeftCell="A2" activePane="bottomLeft" state="frozen"/></sheetView></sheetViews>""");
        xml.Append("<sheetData>");
        xml.Append("<row r=\"1\">");
        for (var c = 0; c < columns.Count; c++)
        {
            xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{Reference(c, 1)}\" t=\"inlineStr\" s=\"1\"><is><t>{Escape(columns[c])}</t></is></c>");
        }

        xml.Append("</row>");
        for (var r = 0; r < rows.Count; r++)
        {
            var number = r + 2;
            xml.Append(CultureInfo.InvariantCulture, $"<row r=\"{number}\">");
            for (var c = 0; c < rows[r].Count && c < columns.Count; c++)
            {
                var cell = Reference(c, number);
                switch (rows[r][c])
                {
                    case null:
                        break;
                    case bool b:
                        xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{cell}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                        break;
                    case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                        xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{cell}\"><v>{DataExporter.Scalar(rows[r][c])}</v></c>");
                        break;
                    case var value:
                        xml.Append(CultureInfo.InvariantCulture, $"<c r=\"{cell}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Escape(DataExporter.Scalar(value))}</t></is></c>");
                        break;
                }
            }

            xml.Append("</row>");
        }

        xml.Append("</sheetData></worksheet>");
        return xml.ToString();
    }

    // Column index 0 → A, 26 → AA.
    private static string Reference(int column, int row)
    {
        var name = string.Empty;
        for (var n = column + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + ((n - 1) % 26)) + name;
        }

        return name + row.ToString(CultureInfo.InvariantCulture);
    }

    // XML 1.0 forbids most control characters even when escaped.
    private static string Escape(string value) =>
        SecurityElement.Escape(new string(value.Where(c => c is '\t' or '\n' or '\r' || c >= ' ').ToArray()));

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content.Trim());
    }
}
