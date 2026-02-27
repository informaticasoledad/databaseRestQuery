using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using Microsoft.Extensions.Options;

namespace DatabaseRestQuery.Api.Services;

public sealed class QueryRequestValidator(IOptions<QueueOptions> options) : IQueryRequestValidator
{
    private static readonly HashSet<string> SupportedServerTypes =
    [
        "sqlserver",
        "sqlserverlegacy",
        "freetds",
        "postgresql",
        "mysql",
        "db2iseries"
    ];

    private readonly QueueOptions _options = options.Value;
    private static readonly HashSet<string> SupportedResponseFormats =
    [
        "json",
        "jsonl",
        "xml",
        "toon",
        "htmltable",
        "csvtab",
        "csvcomma",
        "csvpipeline"
    ];

    public IReadOnlyList<string> Validate(QueryRequest request)
    {
        var errors = new List<string>();

        var hasServer = request.Server is not null;
        var hasConnectionName = !string.IsNullOrWhiteSpace(request.ConnectionName);

        if (!hasServer && !hasConnectionName)
        {
            errors.Add("Debe enviar server o connectionName.");
            return errors;
        }

        if (hasServer)
        {
            if (string.IsNullOrWhiteSpace(request.Server!.Type))
            {
                errors.Add("server.type es obligatorio.");
            }
            else
            {
                var normalizedServerType = NormalizeServerType(request.Server!.Type);
                if (!SupportedServerTypes.Contains(normalizedServerType))
                {
                    errors.Add("server.type no soportado. Valores permitidos: sqlserver, sqlserver-legacy, freetds, postgresql, mysql, db2-iseries.");
                }
            }

            if (string.IsNullOrWhiteSpace(request.Server!.Connstr))
            {
                errors.Add("server.connstr es obligatorio.");
            }
        }
        else if (string.IsNullOrWhiteSpace(request.ConnectionName))
        {
            errors.Add("connectionName es obligatorio cuando no se envia server.");
        }

        var commandText = request.Command?.CommandText;
        var queryText = request.Query;
        if (string.IsNullOrWhiteSpace(commandText) && string.IsNullOrWhiteSpace(queryText))
        {
            errors.Add("Debe enviar query o command.commandText.");
        }

        var effectiveText = !string.IsNullOrWhiteSpace(commandText) ? commandText : queryText;
        if (!string.IsNullOrWhiteSpace(effectiveText) && effectiveText.Length > _options.MaxCommandTextLength)
        {
            errors.Add($"La consulta/comando excede el maximo permitido ({_options.MaxCommandTextLength} caracteres).");
        }

        if (request.RowsLimit < 0)
        {
            errors.Add("rowsLimit no puede ser negativo.");
        }
        else if (request.RowsLimit > _options.MaxRowsLimit)
        {
            errors.Add($"rowsLimit excede el maximo permitido ({_options.MaxRowsLimit}).");
        }

        if (request.ExecutionTimeout < 0)
        {
            errors.Add("executionTimeout no puede ser negativo.");
        }
        else if (request.ExecutionTimeout > _options.MaxExecutionTimeoutSeconds)
        {
            errors.Add($"executionTimeout excede el maximo permitido ({_options.MaxExecutionTimeoutSeconds} segundos).");
        }

        var commandTimeout = request.Command?.CommandTimeout ?? 30;
        if (commandTimeout < 0)
        {
            errors.Add("command.commandTimeout no puede ser negativo.");
        }
        else if (commandTimeout > _options.MaxCommandTimeoutSeconds)
        {
            errors.Add($"command.commandTimeout excede el maximo permitido ({_options.MaxCommandTimeoutSeconds} segundos).");
        }

        var parameters = request.Command?.Params;
        if (parameters is not null)
        {
            if (parameters.Count > _options.MaxParamsCount)
            {
                errors.Add($"command.params excede el maximo permitido ({_options.MaxParamsCount}).");
            }

            if (parameters.Any(p => string.IsNullOrWhiteSpace(p.Name)))
            {
                errors.Add("Todos los parametros en command.params deben tener name.");
            }
        }

        if (request.StreamResult && request.UseQueue)
        {
            errors.Add("streamResult solo esta soportado cuando useQueue=false.");
        }

        if (request.StreamResult && request.CompressResult)
        {
            errors.Add("streamResult y compressResult no se pueden usar al mismo tiempo.");
        }

        var normalizedResponseFormat = NormalizeResponseFormat(request.ResponseFormat);
        if (!SupportedResponseFormats.Contains(normalizedResponseFormat))
        {
            errors.Add("responseFormat no soportado. Valores permitidos: json, jsonl, xml, toon, html_table, csv_tab, csv_comma, csv_pipeline.");
        }

        if (request.CompressResult && normalizedResponseFormat != "json")
        {
            errors.Add("compressResult solo puede usarse con responseFormat=json.");
        }

        if (request.StreamResult && normalizedResponseFormat != "json")
        {
            errors.Add("streamResult solo puede usarse con responseFormat=json.");
        }

        if (request.ExportToS3)
        {
            var normalizedExportFormat = NormalizeResponseFormat(request.ExportFormat);
            if (normalizedExportFormat is not ("json" or "jsonl" or "csvtab" or "csvcomma" or "csvpipeline"))
            {
                errors.Add("exportFormat no soportado. Valores permitidos: json, jsonl, csv_tab, csv_comma, csv_pipeline.");
            }

            if (request.StreamResult)
            {
                errors.Add("exportToS3 no puede usarse con streamResult=true.");
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ResponseQueueCallback))
        {
            if (!request.UseQueue)
            {
                errors.Add("responseQueueCallback solo aplica cuando useQueue=true.");
            }

            if (!Uri.TryCreate(request.ResponseQueueCallback, UriKind.Absolute, out var callbackUri) ||
                (callbackUri.Scheme != Uri.UriSchemeHttp && callbackUri.Scheme != Uri.UriSchemeHttps))
            {
                errors.Add("responseQueueCallback debe ser una URL absoluta http/https.");
            }
        }

        return errors;
    }

    private static string NormalizeServerType(string serverType)
    {
        return serverType
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string NormalizeResponseFormat(string? responseFormat)
    {
        return string.IsNullOrWhiteSpace(responseFormat)
            ? "json"
            : responseFormat
                .Trim()
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace("-", string.Empty, StringComparison.Ordinal)
                .ToLowerInvariant();
    }
}
