using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DatabaseRestQuery.Api.Infrastructure;

public static class ResultPayloadSerializer
{
    public static string NormalizeFormat(string? format)
    {
        return string.IsNullOrWhiteSpace(format)
            ? "json"
            : format.Trim().ToLowerInvariant();
    }

    public static (byte[] Bytes, string ContentType, string Extension) Serialize(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string? format)
    {
        var normalized = NormalizeFormat(format);
        return normalized switch
        {
            "jsonl" => (Encoding.UTF8.GetBytes(ToJsonLines(rows)), "application/x-ndjson", "jsonl"),
            "csv_tab" => (Encoding.UTF8.GetBytes(ToCsv(rows, '\t')), "text/csv", "csv"),
            "csv_comma" => (Encoding.UTF8.GetBytes(ToCsv(rows, ',')), "text/csv", "csv"),
            "csv_pipeline" => (Encoding.UTF8.GetBytes(ToCsv(rows, '|')), "text/csv", "csv"),
            _ => (Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rows)), "application/json", "json")
        };
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
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.Append(JsonSerializer.Serialize(row)).Append('\n');
        }

        return sb.ToString();
    }

    private static string ToCsv(IReadOnlyList<Dictionary<string, object?>> rows, char separator)
    {
        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var headers = new List<string>();
        foreach (var row in rows)
        {
            foreach (var key in row.Keys)
            {
                if (!headers.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    headers.Add(key);
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(string.Join(separator, headers.Select(h => Escape(h, separator))));

        foreach (var row in rows)
        {
            var line = headers.Select(h =>
            {
                row.TryGetValue(h, out var value);
                return Escape(value?.ToString() ?? string.Empty, separator);
            });

            sb.AppendLine(string.Join(separator, line));
        }

        return sb.ToString();
    }

    private static string Escape(string value, char separator)
    {
        var needsQuotes = value.Contains(separator) || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var normalized = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{normalized}\"" : normalized;
    }
}
