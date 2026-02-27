using DatabaseRestQuery.Api.Infrastructure;
using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using DatabaseRestQuery.Api.Services;
using Prometheus;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var configuredQueueOptions = builder.Configuration.GetSection(QueueOptions.SectionName).Get<QueueOptions>() ?? new QueueOptions();

builder.Services.Configure<QueueOptions>(builder.Configuration.GetSection(QueueOptions.SectionName));
builder.Services.Configure<List<ServerConnectionItem>>(builder.Configuration.GetSection("ServerConnections"));
builder.Services.Configure<S3ExportOptions>(builder.Configuration.GetSection(S3ExportOptions.SectionName));
builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<IDataSourceCircuitBreaker, DataSourceCircuitBreaker>();
builder.Services.AddSingleton<IAppMetrics, AppMetrics>();
builder.Services.AddSingleton<IDatabaseExecutor, DatabaseExecutor>();
builder.Services.AddSingleton<IQueueRepository, SqliteQueueRepository>();
builder.Services.AddSingleton<IQueueResponseWaiter, QueueResponseWaiter>();
builder.Services.AddSingleton<IQueryRequestValidator, QueryRequestValidator>();
builder.Services.AddSingleton<IRequestHistoryRepository, SqliteRequestHistoryRepository>();
builder.Services.AddSingleton<IResponseQueueCallbackSender, ResponseQueueCallbackSender>();
builder.Services.AddSingleton<IResultExportStorage, S3ResultExportStorage>();
builder.Services.AddHttpClient("response-queue-callback", httpClient =>
{
    httpClient.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<BufferedQueueEnqueuer>();
builder.Services.AddSingleton<IQueueEnqueuer>(sp => sp.GetRequiredService<BufferedQueueEnqueuer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BufferedQueueEnqueuer>());

if (!IsApiOnlyMode(configuredQueueOptions.RunMode))
{
    builder.Services.AddHostedService<QueueWorkerHostedService>();
}

builder.Services.AddOpenApi();

var app = builder.Build();

if (!IsWorkerOnlyMode(configuredQueueOptions.RunMode))
{
    app.MapOpenApi();
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

await app.Services.GetRequiredService<IQueueRepository>().InitializeAsync();
await app.Services.GetRequiredService<IRequestHistoryRepository>().InitializeAsync();

app.UseHttpMetrics();
app.MapMetrics("/metrics");

if (!IsWorkerOnlyMode(configuredQueueOptions.RunMode))
{
    app.MapPost("/doQuery", async (QueryRequest request, IDatabaseExecutor executor, IQueueRepository queueRepository, IQueueEnqueuer queueEnqueuer, IQueueResponseWaiter waiter, IQueryRequestValidator validator, IRequestHistoryRepository historyRepository, IAppMetrics metrics, IResultExportStorage exportStorage, Microsoft.Extensions.Options.IOptions<QueueOptions> options, Microsoft.Extensions.Options.IOptions<List<ServerConnectionItem>> serverConnectionsOptions, HttpContext httpContext, CancellationToken cancellationToken) =>
    {
        if (!TryResolveRequestServer(request, serverConnectionsOptions.Value, out var effectiveRequest, out var resolveError))
        {
            return Results.BadRequest(new
            {
                ok = false,
                message = resolveError
            });
        }

        var validationErrors = validator.Validate(effectiveRequest);
        if (validationErrors.Count > 0)
        {
            return Results.BadRequest(new
            {
                ok = false,
                message = "Request invalido.",
                errors = validationErrors
            });
        }

        var normalizedRequest = effectiveRequest with
        {
            TransactionId = string.IsNullOrWhiteSpace(effectiveRequest.TransactionId)
                ? Guid.NewGuid().ToString("N")
                : effectiveRequest.TransactionId.Trim()
        };
        await historyRepository.UpsertRequestAsync(
            normalizedRequest.TransactionId!,
            normalizedRequest,
            normalizedRequest.UseQueue ? "queue" : "direct",
            cancellationToken);

        if (normalizedRequest.UseQueue)
        {
            var stats = await queueRepository.GetStatsAsync(cancellationToken);
            var inFlight = stats.Pending + stats.Processing;
            if (inFlight >= Math.Max(1, options.Value.BackpressureMaxInFlight))
            {
                metrics.IncrementBackpressureReject();
                return ResponseFormatter.Format(new QueryResponse(
                    normalizedRequest.TransactionId!,
                    false,
                    "Servicio ocupado. Cola en backpressure, reintente mas tarde.",
                    [],
                    null
                ), normalizedRequest.ResponseFormat, StatusCodes.Status429TooManyRequests);
            }

            try
            {
                await queueEnqueuer.EnqueueAsync(normalizedRequest, forceImmediate: normalizedRequest.WaitForResponse, cancellationToken);
                metrics.IncrementEnqueued(normalizedRequest.WaitForResponse ? "sync-wait" : "async-buffered");
            }
            catch (InvalidOperationException ex)
            {
                return ResponseFormatter.Format(new QueryResponse(
                    normalizedRequest.TransactionId!,
                    false,
                    ex.Message,
                    [],
                    null
                ), normalizedRequest.ResponseFormat, StatusCodes.Status409Conflict);
            }

            if (!normalizedRequest.WaitForResponse)
            {
                var queuedResponse = new QueryResponse(
                    normalizedRequest.TransactionId!,
                    true,
                    "Peticion encolada.",
                    [],
                    null
                );
                await historyRepository.UpsertResponseAsync(normalizedRequest.TransactionId!, queuedResponse, true, queuedResponse.Message, cancellationToken);
                return ResponseFormatter.Format(queuedResponse, normalizedRequest.ResponseFormat);
            }

            var waitSeconds = normalizedRequest.ExecutionTimeout > 0 ? normalizedRequest.ExecutionTimeout : 30;
            var job = await waiter.WaitForCompletionAsync(normalizedRequest.TransactionId!, TimeSpan.FromSeconds(waitSeconds), cancellationToken);

            if (job is null)
            {
                var pendingResponse = new QueryResponse(
                    normalizedRequest.TransactionId!,
                    true,
                    "Peticion encolada, aun en procesamiento.",
                    [],
                    null
                );
                await historyRepository.UpsertResponseAsync(normalizedRequest.TransactionId!, pendingResponse, true, pendingResponse.Message, cancellationToken);
                return ResponseFormatter.Format(pendingResponse, normalizedRequest.ResponseFormat);
            }

            return job.Status switch
            {
                QueueJobStatus.Completed => ResponseFormatter.Format(await SaveAndReturnAsync(CreateQueryResponse(
                    job.TransactionId,
                    true,
                    job.Message ?? "Completado",
                    job.Result ?? [],
                    normalizedRequest.CompressResult,
                    job.Export
                ), historyRepository, cancellationToken), normalizedRequest.ResponseFormat),
                QueueJobStatus.Failed => ResponseFormatter.Format(await SaveAndReturnAsync(new QueryResponse(
                    job.TransactionId,
                    false,
                    job.Error ?? job.Message ?? "Error al ejecutar la peticion.",
                    [],
                    null
                ), historyRepository, cancellationToken), normalizedRequest.ResponseFormat),
                _ => ResponseFormatter.Format(await SaveAndReturnAsync(new QueryResponse(
                    job.TransactionId,
                    true,
                    $"Estado actual: {job.Status}",
                    [],
                    null
                ), historyRepository, cancellationToken), normalizedRequest.ResponseFormat)
            };
        }

        try
        {
            if (normalizedRequest.StreamResult)
            {
                httpContext.Response.ContentType = "application/json";
                await using var writer = new Utf8JsonWriter(httpContext.Response.Body);
                writer.WriteStartObject();
                writer.WriteString("transactionId", normalizedRequest.TransactionId);
                writer.WriteBoolean("ok", true);
                writer.WriteString("message", "Consulta ejecutada en modo stream.");
                writer.WriteNull("compressedResult");
                writer.WritePropertyName("result");
                writer.WriteStartArray();

                await foreach (var row in executor.ExecuteStreamAsync(normalizedRequest, cancellationToken))
                {
                    JsonSerializer.Serialize(writer, row);
                    await writer.FlushAsync(cancellationToken);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken);
                return Results.Empty;
            }

            var result = await executor.ExecuteAsync(normalizedRequest, cancellationToken);
            QueryExportInfo? export = null;
            var rows = result.Result;
            var message = result.Message;

            if (normalizedRequest.ExportToS3 && result.Ok)
            {
                export = await exportStorage.ExportAsync(
                    normalizedRequest.TransactionId!,
                    result.Result,
                    normalizedRequest.ExportFormat,
                    normalizedRequest.ExportCompress,
                    cancellationToken);
                rows = [];
                message = $"{result.Message} Resultado exportado a S3.";
            }

            var response = CreateQueryResponse(
                normalizedRequest.TransactionId!,
                result.Ok,
                message,
                rows,
                normalizedRequest.CompressResult,
                export
            );
            await historyRepository.UpsertResponseAsync(normalizedRequest.TransactionId!, response, response.Ok, response.Message, cancellationToken);
            return ResponseFormatter.Format(response, normalizedRequest.ResponseFormat);
        }
        catch (TimeoutException ex)
        {
            var timeoutResponse = new QueryResponse(
                normalizedRequest.TransactionId!,
                false,
                ex.Message,
                [],
                null
            );
            await historyRepository.UpsertResponseAsync(normalizedRequest.TransactionId!, timeoutResponse, timeoutResponse.Ok, timeoutResponse.Message, cancellationToken);
            return ResponseFormatter.Format(timeoutResponse, normalizedRequest.ResponseFormat, StatusCodes.Status408RequestTimeout);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Circuit breaker abierto", StringComparison.OrdinalIgnoreCase))
        {
            metrics.IncrementCircuitOpenReject();
            var cbResponse = new QueryResponse(
                normalizedRequest.TransactionId!,
                false,
                ex.Message,
                [],
                null
            );
            await historyRepository.UpsertResponseAsync(normalizedRequest.TransactionId!, cbResponse, cbResponse.Ok, cbResponse.Message, cancellationToken);
            return ResponseFormatter.Format(cbResponse, normalizedRequest.ResponseFormat, StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception ex)
        {
            var errorResponse = new QueryResponse(
                normalizedRequest.TransactionId!,
                false,
                ex.Message,
                [],
                null
            );
            await historyRepository.UpsertResponseAsync(normalizedRequest.TransactionId!, errorResponse, errorResponse.Ok, errorResponse.Message, cancellationToken);
            return ResponseFormatter.Format(errorResponse, normalizedRequest.ResponseFormat, StatusCodes.Status400BadRequest);
        }
    });

    app.MapGet("/checkResponse/{transactionId}", async (string transactionId, IQueueRepository queueRepository, IRequestHistoryRepository historyRepository, CancellationToken cancellationToken) =>
    {
        var job = await queueRepository.GetJobAsync(transactionId, cancellationToken);

        if (job is null)
        {
            return Results.NotFound(new QueryResponse(transactionId, false, "TransactionId no encontrado.", [], null));
        }

        var response = job.Status switch
        {
            QueueJobStatus.Completed => CreateQueryResponse(
                job.TransactionId,
                true,
                job.Message ?? "Completado",
                job.Result ?? [],
                job.CompressResult,
                job.Export
            ),
            QueueJobStatus.Failed => new QueryResponse(
                job.TransactionId,
                false,
                job.Error ?? job.Message ?? "Error al ejecutar la peticion.",
                [],
                null
            ),
            _ => new QueryResponse(
                job.TransactionId,
                true,
                $"Estado actual: {job.Status}",
                [],
                null
            )
        };
        await historyRepository.UpsertResponseAsync(transactionId, response, response.Ok, response.Message, cancellationToken);
        return ResponseFormatter.Format(response, job.ResponseFormat);
    });

    app.MapGet("/queuePendingJobs", async (IQueueRepository queueRepository, CancellationToken cancellationToken) =>
    {
        var jobs = await queueRepository.GetPendingJobsAsync(cancellationToken);
        return Results.Ok(jobs);
    });

    app.MapPost("/queuePurge", async (IQueueRepository queueRepository, CancellationToken cancellationToken) =>
    {
        var deleted = await queueRepository.PurgePendingAsync(cancellationToken);
        return Results.Ok(new
        {
            ok = true,
            message = $"Se eliminaron {deleted} mensajes pendientes de la cola."
        });
    });

    app.MapGet("/historyRecent", async (int? limit, IRequestHistoryRepository historyRepository, CancellationToken cancellationToken) =>
    {
        var rows = await historyRepository.GetRecentAsync(limit ?? 30, cancellationToken);
        return Results.Ok(rows);
    });
}

app.MapGet("/health", async (IQueueRepository queueRepository, Microsoft.Extensions.Options.IOptions<QueueOptions> options, CancellationToken cancellationToken) =>
{
    var stats = await queueRepository.GetStatsAsync(cancellationToken);
    var queueOptions = options.Value;
    return Results.Ok(new
    {
        ok = true,
        service = "DatabaseRestQuery.Api",
        runMode = queueOptions.RunMode,
        timeUtc = DateTime.UtcNow,
        queue = stats,
        responseQueuePolicy = new
        {
            maxItems = queueOptions.ResponseQueueMaxItems,
            targetItemsAfterPurge = queueOptions.ResponseQueueTargetItemsAfterPurge,
            retentionHours = queueOptions.ResponseRetentionHours
        }
    });
});

app.Run();

static QueryResponse CreateQueryResponse(
    string transactionId,
    bool ok,
    string message,
    IReadOnlyList<Dictionary<string, object?>> result,
    bool compressResult,
    QueryExportInfo? export = null)
{
    if (export is not null)
    {
        return new QueryResponse(transactionId, ok, message, [], null, export);
    }

    if (!compressResult || result.Count == 0)
    {
        return new QueryResponse(transactionId, ok, message, result, null, null);
    }

    var compressed = ResultCompression.ToZipBase64(result);
    return new QueryResponse(
        transactionId,
        ok,
        $"{message} Resultado comprimido en ZIP Base64.",
        [],
        compressed,
        null
    );
}

static bool IsWorkerOnlyMode(string mode) => string.Equals(mode?.Trim(), "Worker", StringComparison.OrdinalIgnoreCase);

static bool IsApiOnlyMode(string mode) => string.Equals(mode?.Trim(), "Api", StringComparison.OrdinalIgnoreCase);

static async Task<QueryResponse> SaveAndReturnAsync(QueryResponse response, IRequestHistoryRepository historyRepository, CancellationToken cancellationToken)
{
    await historyRepository.UpsertResponseAsync(response.TransactionId, response, response.Ok, response.Message, cancellationToken);
    return response;
}

static bool TryResolveRequestServer(
    QueryRequest request,
    IReadOnlyList<ServerConnectionItem>? configuredConnections,
    out QueryRequest resolvedRequest,
    out string? error)
{
    resolvedRequest = request;
    error = null;

    if (request.Server is not null)
    {
        return true;
    }

    if (string.IsNullOrWhiteSpace(request.ConnectionName))
    {
        error = "Debe enviar server o connectionName.";
        return false;
    }

    var connectionName = request.ConnectionName.Trim();
    var connection = configuredConnections?
        .FirstOrDefault(x => string.Equals(x.ConnectionName, connectionName, StringComparison.OrdinalIgnoreCase));

    if (connection is null)
    {
        error = $"connectionName '{connectionName}' no existe en ServerConnections.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(connection.Type) || string.IsNullOrWhiteSpace(connection.Connstr))
    {
        error = $"connectionName '{connectionName}' tiene type/connstr invalido en ServerConnections.";
        return false;
    }

    resolvedRequest = request with
    {
        ConnectionName = connectionName,
        Server = new ServerRequest(connection.Type.Trim(), connection.Connstr.Trim())
    };
    return true;
}
