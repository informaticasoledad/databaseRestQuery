using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;

namespace DatabaseRestQuery.Api.Infrastructure;

public static class ResultPayloadSerializer
{
    public static string NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return "json";
        }

        var normalized = format
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "csvtab" => "csv_tab",
            "csvcomma" => "csv_comma",
            "csvpipeline" => "csv_pipeline",
            "excel" => "xlsx",
            _ => normalized
        };
    }

    public static (byte[] Bytes, string ContentType, string Extension) Serialize(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string? format)
    {
        using var output = new MemoryStream();
        var (contentType, extension) = SerializeToStream(rows, format, output);
        return (output.ToArray(), contentType, extension);
    }

    public static (string ContentType, string Extension) SerializeToStream(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string? format,
        Stream output)
    {
        var normalized = NormalizeFormat(format);
        switch (normalized)
        {
            case "jsonl":
                WriteJsonLines(rows, output);
                return ("application/x-ndjson", "jsonl");
            case "csv_tab":
                WriteCsv(rows, '\t', output);
                return ("text/csv", "csv");
            case "csv_comma":
                WriteCsv(rows, ',', output);
                return ("text/csv", "csv");
            case "csv_pipeline":
                WriteCsv(rows, '|', output);
                return ("text/csv", "csv");
            case "xlsx":
                WriteExcelOpenXml(rows, output);
                return ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx");
            default:
                JsonSerializer.Serialize(output, rows);
                return ("application/json", "json");
        }
    }

    public static byte[] Gzip(byte[] input)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(input, 0, input.Length);
        }

        return output.ToArray();
    }

    public static string ToJsonLines(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        using var stream = new MemoryStream();
        WriteJsonLines(rows, stream);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteJsonLines(IReadOnlyList<Dictionary<string, object?>> rows, Stream output)
    {
        if (rows.Count == 0)
        {
            return;
        }

        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);
        foreach (var row in rows)
        {
            writer.Write(JsonSerializer.Serialize(row));
            writer.Write('\n');
        }

        writer.Flush();
    }

    private static void WriteCsv(IReadOnlyList<Dictionary<string, object?>> rows, char separator, Stream output)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var headers = GetHeaders(rows);
        using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), leaveOpen: true);

        writer.WriteLine(string.Join(separator, headers.Select(h => Escape(h, separator))));

        foreach (var row in rows)
        {
            var line = headers.Select(h =>
            {
                row.TryGetValue(h, out var value);
                return Escape(value?.ToString() ?? string.Empty, separator);
            });

            writer.WriteLine(string.Join(separator, line));
        }

        writer.Flush();
    }

    private static List<string> GetHeaders(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var headers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                {
                    headers.Add(key);
                }
            }
        }

        return headers;
    }

    private static string Escape(string value, char separator)
    {
        var needsQuotes = value.Contains(separator) || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var normalized = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{normalized}\"" : normalized;
    }

    private static void WriteExcelOpenXml(IReadOnlyList<Dictionary<string, object?>> rows, Stream output)
    {
        var headers = GetHeaders(rows);

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(archive, "[Content_Types].xml", BuildContentTypesXml());
            WriteZipEntry(archive, "_rels/.rels", BuildRootRelsXml());
            WriteZipEntry(archive, "xl/workbook.xml", BuildWorkbookXml());
            WriteZipEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelsXml());
            WriteWorksheetEntry(archive, "xl/worksheets/sheet1.xml", headers, rows);
            WriteZipEntry(archive, "docProps/core.xml", BuildCorePropsXml());
            WriteZipEntry(archive, "docProps/app.xml", BuildAppPropsXml());
        }
    }

    private static void WriteZipEntry(ZipArchive archive, string entryName, string xmlContent)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(xmlContent);
    }

    private static string BuildContentTypesXml()
    {
        return """
               <?xml version="1.0" encoding="UTF-8"?>
               <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                 <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                 <Default Extension="xml" ContentType="application/xml"/>
                 <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                 <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                 <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
                 <Override PartName="/docProps/app.xml" ContentType="application/vnd.openxmlformats-officedocument.extended-properties+xml"/>
               </Types>
               """;
    }

    private static string BuildRootRelsXml()
    {
        return """
               <?xml version="1.0" encoding="UTF-8"?>
               <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                 <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                 <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
                 <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties" Target="docProps/app.xml"/>
               </Relationships>
               """;
    }

    private static string BuildWorkbookXml()
    {
        return """
               <?xml version="1.0" encoding="UTF-8"?>
               <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                 <sheets>
                   <sheet name="Result" sheetId="1" r:id="rId1"/>
                 </sheets>
               </workbook>
               """;
    }

    private static string BuildWorkbookRelsXml()
    {
        return """
               <?xml version="1.0" encoding="UTF-8"?>
               <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                 <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
               </Relationships>
               """;
    }

    private static string BuildCorePropsXml()
    {
        var utcNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
                  <dc:creator>DatabaseRestQuery</dc:creator>
                  <cp:lastModifiedBy>DatabaseRestQuery</cp:lastModifiedBy>
                  <dcterms:created xsi:type="dcterms:W3CDTF">{utcNow}</dcterms:created>
                  <dcterms:modified xsi:type="dcterms:W3CDTF">{utcNow}</dcterms:modified>
                </cp:coreProperties>
                """;
    }

    private static string BuildAppPropsXml()
    {
        return """
               <?xml version="1.0" encoding="UTF-8"?>
               <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties" xmlns:vt="http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes">
                 <Application>DatabaseRestQuery</Application>
               </Properties>
               """;
    }

    private static void WriteWorksheetEntry(ZipArchive archive, string entryName, IReadOnlyList<string> headers, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.SmallestSize);
        using var entryStream = entry.Open();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false
        };

        using var writer = XmlWriter.Create(entryStream, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        writer.WriteStartElement("sheetData");

        var rowIndex = 1;
        if (headers.Count > 0)
        {
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", rowIndex.ToString());
            for (var columnIndex = 0; columnIndex < headers.Count; columnIndex++)
            {
                WriteCell(writer, rowIndex, columnIndex + 1, headers[columnIndex], forceText: true);
            }

            writer.WriteEndElement();
            rowIndex++;
        }

        foreach (var row in rows)
        {
            writer.WriteStartElement("row");
            writer.WriteAttributeString("r", rowIndex.ToString());
            for (var columnIndex = 0; columnIndex < headers.Count; columnIndex++)
            {
                var header = headers[columnIndex];
                row.TryGetValue(header, out var value);
                WriteCell(writer, rowIndex, columnIndex + 1, value, forceText: false);
            }

            writer.WriteEndElement();
            rowIndex++;
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndDocument();
        writer.Flush();
    }

    private static void WriteCell(XmlWriter writer, int rowIndex, int columnIndex, object? value, bool forceText)
    {
        writer.WriteStartElement("c");
        writer.WriteAttributeString("r", $"{ToColumnName(columnIndex)}{rowIndex}");

        if (!forceText && TryWriteNumericCell(writer, value))
        {
            writer.WriteEndElement();
            return;
        }

        if (!forceText && value is bool boolValue)
        {
            writer.WriteAttributeString("t", "b");
            writer.WriteElementString("v", boolValue ? "1" : "0");
            writer.WriteEndElement();
            return;
        }

        writer.WriteAttributeString("t", "inlineStr");
        writer.WriteStartElement("is");
        writer.WriteElementString("t", value?.ToString() ?? string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static bool TryWriteNumericCell(XmlWriter writer, object? value)
    {
        switch (value)
        {
            case byte b:
                writer.WriteElementString("v", b.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case sbyte sb:
                writer.WriteElementString("v", sb.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case short s:
                writer.WriteElementString("v", s.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case ushort us:
                writer.WriteElementString("v", us.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case int i:
                writer.WriteElementString("v", i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case uint ui:
                writer.WriteElementString("v", ui.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case long l:
                writer.WriteElementString("v", l.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case ulong ul:
                writer.WriteElementString("v", ul.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case float f:
                writer.WriteElementString("v", f.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case double d:
                writer.WriteElementString("v", d.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case decimal dec:
                writer.WriteElementString("v", dec.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return true;
            default:
                return false;
        }
    }

    private static string ToColumnName(int columnIndex)
    {
        var dividend = columnIndex;
        var columnName = string.Empty;

        while (dividend > 0)
        {
            var modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar('A' + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName;
    }
}
