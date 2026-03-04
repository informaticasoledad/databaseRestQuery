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
            "jsonl" => ToJsonLinesResult(response, statusCode),
            "xml" => Results.Content(ToXml(response), "application/xml; charset=utf-8", Encoding.UTF8, statusCode ?? StatusCodes.Status200OK),
            "toon" => Results.Text(ToToon(response), "text/plain; charset=utf-8", Encoding.UTF8, statusCode ?? StatusCodes.Status200OK),
            "html_table" => Results.Content(ToHtmlTable(response), "text/html; charset=utf-8", Encoding.UTF8, statusCode ?? StatusCodes.Status200OK),
            "csv_tab" => ToCsvResult(response, '\t', statusCode),
            "csv_comma" => ToCsvResult(response, ',', statusCode),
            "csv_pipeline" => ToCsvResult(response, '|', statusCode),
            "excel" => ToExcelResult(response, statusCode),
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
        if (string.IsNullOrWhiteSpace(responseFormat))
        {
            return "json";
        }

        var normalized = responseFormat
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();

        return normalized switch
        {
            "htmltable" => "html_table",
            "csvtab" => "csv_tab",
            "csvcomma" => "csv_comma",
            "csvpipeline" => "csv_pipeline",
            "xlsx" => "excel",
            _ => normalized
        };
    }

    private static IResult ToCsvResult(QueryResponse response, char separator, int? statusCode)
    {
        if (!response.Ok)
        {
            var errorCsv = $"transactionId{separator}ok{separator}message\n{response.TransactionId}{separator}false{separator}{response.Message}\n";
            return Results.Text(errorCsv, "text/csv; charset=utf-8", Encoding.UTF8, statusCode ?? StatusCodes.Status400BadRequest);
        }

        var format = separator switch
        {
            '\t' => "csv_tab",
            ',' => "csv_comma",
            _ => "csv_pipeline"
        };
        var downloadName = $"result_{response.TransactionId}.csv";

        if (statusCode is { } code && (code < 200 || code > 299))
        {
            return new StreamingResult("text/csv; charset=utf-8", code, null, output =>
            {
                ResultPayloadSerializer.SerializeToStream(response.Result, format, output);
                return Task.CompletedTask;
            });
        }

        return new StreamingResult("text/csv; charset=utf-8", statusCode ?? StatusCodes.Status200OK, downloadName, output =>
        {
            ResultPayloadSerializer.SerializeToStream(response.Result, format, output);
            return Task.CompletedTask;
        });
    }

    private static IResult ToJsonLinesResult(QueryResponse response, int? statusCode)
    {
        if (!response.Ok)
        {
            var errorLine = JsonSerializer.Serialize(new
            {
                transactionId = response.TransactionId,
                ok = response.Ok,
                message = response.Message
            }) + "\n";
            return Results.Text(errorLine, "application/x-ndjson; charset=utf-8", Encoding.UTF8, statusCode ?? StatusCodes.Status400BadRequest);
        }

        if (response.Export is not null && response.Result.Count == 0)
        {
            var exportLine = JsonSerializer.Serialize(new
            {
                transactionId = response.TransactionId,
                ok = response.Ok,
                message = response.Message,
                export = response.Export
            }) + "\n";
            return Results.Text(exportLine, "application/x-ndjson; charset=utf-8", Encoding.UTF8, statusCode ?? StatusCodes.Status200OK);
        }

        if (statusCode is { } code && (code < 200 || code > 299))
        {
            return new StreamingResult("application/x-ndjson; charset=utf-8", code, null, output =>
            {
                ResultPayloadSerializer.SerializeToStream(response.Result, "jsonl", output);
                return Task.CompletedTask;
            });
        }

        return new StreamingResult("application/x-ndjson; charset=utf-8", statusCode ?? StatusCodes.Status200OK, $"result_{response.TransactionId}.jsonl", output =>
        {
            ResultPayloadSerializer.SerializeToStream(response.Result, "jsonl", output);
            return Task.CompletedTask;
        });
    }

    private static IResult ToExcelResult(QueryResponse response, int? statusCode)
    {
        if (!response.Ok)
        {
            return statusCode.HasValue
                ? Results.Json(response, statusCode: statusCode)
                : Results.BadRequest(response);
        }

        return new StreamingResult("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", statusCode ?? StatusCodes.Status200OK, $"result_{response.TransactionId}.xlsx", output =>
        {
            ResultPayloadSerializer.SerializeToStream(response.Result, "excel", output);
            return Task.CompletedTask;
        });
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

        var headers = GetHeaders(response.Result);

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

    private sealed class StreamingResult(
        string contentType,
        int statusCode,
        string? fileDownloadName,
        Func<Stream, Task> writeBody) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = contentType;
            if (!string.IsNullOrWhiteSpace(fileDownloadName))
            {
                httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{fileDownloadName}\"";
            }

            await writeBody(httpContext.Response.Body);
        }
    }
}
