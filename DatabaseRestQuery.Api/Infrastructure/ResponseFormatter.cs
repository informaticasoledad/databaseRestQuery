using DatabaseRestQuery.Api.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DatabaseRestQuery.Api.Infrastructure;

public static class ResponseFormatter
{
    public static IResult Format(QueryResponse response, string? responseFormat, int? statusCode = null)
    {
        var normalized = Normalize(responseFormat);

        return normalized switch
        {
            "json" => statusCode.HasValue
                ? Results.Json(response, statusCode: statusCode)
                : Results.Ok(response),
            "xml" => Results.Content(ToXml(response), "application/xml; charset=utf-8", Encoding.UTF8, statusCode ?? StatusCodes.Status200OK),
            "toon" => Results.Text(ToToon(response), "text/plain; charset=utf-8", Encoding.UTF8, statusCode ?? StatusCodes.Status200OK),
            "html_table" => Results.Content(ToHtmlTable(response), "text/html; charset=utf-8", Encoding.UTF8, statusCode ?? StatusCodes.Status200OK),
            "csv_tab" => ToCsvResult(response, '\t', statusCode),
            "csv_comma" => ToCsvResult(response, ',', statusCode),
            "csv_pipeline" => ToCsvResult(response, '|', statusCode),
            _ => statusCode.HasValue
                ? Results.Json(response, statusCode: statusCode)
                : Results.Ok(response)
        };
    }

    public static bool IsCsv(string? responseFormat)
    {
        var normalized = Normalize(responseFormat);
        return normalized is "csv_tab" or "csv_comma" or "csv_pipeline";
    }

    private static string Normalize(string? responseFormat)
    {
        return string.IsNullOrWhiteSpace(responseFormat)
            ? "json"
            : responseFormat.Trim().ToLowerInvariant();
    }

    private static IResult ToCsvResult(QueryResponse response, char separator, int? statusCode)
    {
        var csv = ToCsv(response, separator);
        var bytes = Encoding.UTF8.GetBytes(csv);

        if (statusCode is { } code && (code < 200 || code > 299))
        {
            return Results.Text(csv, "text/csv; charset=utf-8", Encoding.UTF8, code);
        }

        return Results.File(bytes, "text/csv; charset=utf-8", $"result_{response.TransactionId}.csv");
    }

    private static string ToXml(QueryResponse response)
    {
        var doc = new XDocument(
            new XElement("response",
                new XElement("transactionId", response.TransactionId),
                new XElement("ok", response.Ok),
                new XElement("message", response.Message),
                new XElement("compressedResult", response.CompressedResult ?? string.Empty),
                new XElement("result",
                    response.Result.Select(row =>
                        new XElement("row",
                            row.Select(cell => new XElement(cell.Key, cell.Value?.ToString() ?? string.Empty))
                        )))));

        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static string ToToon(QueryResponse response)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"transactionId: {response.TransactionId}");
        sb.AppendLine($"ok: {response.Ok}");
        sb.AppendLine($"message: {response.Message}");
        sb.AppendLine($"compressedResult: {(string.IsNullOrWhiteSpace(response.CompressedResult) ? "null" : "present")}");
        sb.AppendLine($"resultCount: {response.Result.Count}");

        for (var i = 0; i < response.Result.Count; i++)
        {
            var rowJson = JsonSerializer.Serialize(response.Result[i]);
            sb.AppendLine($"row[{i}]: {rowJson}");
        }

        return sb.ToString();
    }

    private static string ToCsv(QueryResponse response, char separator)
    {
        if (!response.Ok)
        {
            return $"transactionId{separator}ok{separator}message\n{Escape(response.TransactionId, separator)}{separator}false{separator}{Escape(response.Message, separator)}\n";
        }

        if (response.Result.Count == 0)
        {
            return string.Empty;
        }

        var headers = new List<string>();
        foreach (var row in response.Result)
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

        foreach (var row in response.Result)
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

    private static string ToHtmlTable(QueryResponse response)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"/><title>Result</title>");
        sb.Append("<style>body{font-family:Arial,sans-serif;padding:16px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccc;padding:6px;text-align:left}th{background:#f5f5f5}</style>");
        sb.Append("</head><body>");
        sb.Append($"<h3>transactionId: {WebUtility.HtmlEncode(response.TransactionId)}</h3>");
        sb.Append($"<p>ok: {response.Ok}</p>");
        sb.Append($"<p>message: {WebUtility.HtmlEncode(response.Message)}</p>");

        if (!response.Ok)
        {
            sb.Append("</body></html>");
            return sb.ToString();
        }

        var headers = new List<string>();
        foreach (var row in response.Result)
        {
            foreach (var key in row.Keys)
            {
                if (!headers.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    headers.Add(key);
                }
            }
        }

        sb.Append("<table><thead><tr>");
        foreach (var h in headers)
        {
            sb.Append("<th>").Append(WebUtility.HtmlEncode(h)).Append("</th>");
        }
        sb.Append("</tr></thead><tbody>");

        foreach (var row in response.Result)
        {
            sb.Append("<tr>");
            foreach (var h in headers)
            {
                row.TryGetValue(h, out var value);
                sb.Append("<td>").Append(WebUtility.HtmlEncode(value?.ToString() ?? string.Empty)).Append("</td>");
            }
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table></body></html>");
        return sb.ToString();
    }

    private static string Escape(string value, char separator)
    {
        var needsQuotes = value.Contains(separator) || value.Contains('"') || value.Contains('\n') || value.Contains('\r');
        var normalized = value.Replace("\"", "\"\"");
        return needsQuotes ? $"\"{normalized}\"" : normalized;
    }
}
