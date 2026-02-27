using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using Microsoft.Extensions.Options;

namespace DatabaseRestQuery.Api.Services;

public sealed class QueueWorkerHostedService(
    ILogger<QueueWorkerHostedService> logger,
    IQueueRepository queueRepository,
    IDatabaseExecutor databaseExecutor,
    IResultExportStorage exportStorage,
    IRequestHistoryRepository historyRepository,
    IResponseQueueCallbackSender callbackSender,
    IAppMetrics metrics,
    IOptions<QueueOptions> options) : BackgroundService
{
    private readonly QueueOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var workersCount = Math.Max(1, _options.WorkersCount);
        var workers = new List<Task>(workersCount);

        for (var i = 1; i <= workersCount; i++)
        {
            var workerId = $"worker-{i}";
            workers.Add(RunWorkerLoopAsync(workerId, i - 1, workersCount, stoppingToken));
        }

        await Task.WhenAll(workers);
    }

    private async Task RunWorkerLoopAsync(string workerId, int workerIndex, int workerCount, CancellationToken stoppingToken)
    {
        var pollDelay = TimeSpan.FromMilliseconds(Math.Max(100, _options.PollIntervalMs));
        var cleanupInterval = TimeSpan.FromSeconds(Math.Max(10, _options.CleanupIntervalSeconds));
        var nextCleanupAt = DateTime.UtcNow;

        while (!stoppingToken.IsCancellationRequested)
        {
            QueueClaimedJob? claimedJob = null;

            try
            {
                if (DateTime.UtcNow >= nextCleanupAt)
                {
                    var deleted = await queueRepository.CleanupFinishedAsync(stoppingToken);
                    if (deleted > 0)
                    {
                        logger.LogInformation("{WorkerId} limpio {Deleted} jobs finalizados antiguos", workerId, deleted);
                    }

                    nextCleanupAt = DateTime.UtcNow.Add(cleanupInterval);
                }

                claimedJob = await queueRepository.ClaimNextPendingAsync(workerId, workerIndex, workerCount, stoppingToken);

                if (claimedJob is null)
                {
                    await Task.Delay(pollDelay, stoppingToken);
                    continue;
                }

                logger.LogInformation("{WorkerId} procesando transactionId {TransactionId}", workerId, claimedJob.TransactionId);
                var executionResult = await databaseExecutor.ExecuteAsync(claimedJob.Request, stoppingToken);
                QueryExportInfo? export = null;
                var responseRows = executionResult.Result;
                var responseMessage = executionResult.Message;

                if (claimedJob.Request.ExportToS3)
                {
                    export = await exportStorage.ExportAsync(
                        claimedJob.TransactionId,
                        executionResult.Result,
                        claimedJob.Request.ExportFormat,
                        claimedJob.Request.ExportCompress,
                        stoppingToken);
                    responseRows = [];
                    responseMessage = $"{executionResult.Message} Resultado exportado a S3.";
                }

                await queueRepository.MarkCompletedAsync(
                    claimedJob.TransactionId,
                    responseMessage,
                    responseRows,
                    export,
                    stoppingToken);

                var successResponse = new QueryResponse(
                    claimedJob.TransactionId,
                    true,
                    responseMessage,
                    responseRows,
                    null,
                    export);

                await historyRepository.UpsertResponseAsync(
                    claimedJob.TransactionId,
                    successResponse,
                    true,
                    responseMessage,
                    stoppingToken);

                try
                {
                    await callbackSender.SendAsync(
                        claimedJob.Request,
                        successResponse,
                        stoppingToken);
                }
                catch (Exception callbackEx)
                {
                    logger.LogWarning(callbackEx, "No se pudo enviar responseQueueCallback para transactionId {TransactionId}", claimedJob.TransactionId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error procesando la cola en {WorkerId}", workerId);

                if (claimedJob is null)
                {
                    await Task.Delay(pollDelay, stoppingToken);
                    continue;
                }

                var retried = await queueRepository.RetryOrFailAsync(
                    claimedJob.TransactionId,
                    ex.Message,
                    stoppingToken);

                if (retried)
                {
                    metrics.IncrementRetries();
                    logger.LogWarning("{WorkerId} reencolo transactionId {TransactionId} por error", workerId, claimedJob.TransactionId);
                }
                else
                {
                    await historyRepository.UpsertResponseAsync(
                        claimedJob.TransactionId,
                        new QueryResponse(
                            claimedJob.TransactionId,
                            false,
                            "Error en procesamiento de cola.",
                            [],
                            null),
                        false,
                        ex.Message,
                        stoppingToken);

                    try
                    {
                        await callbackSender.SendAsync(
                            claimedJob.Request,
                            new QueryResponse(
                                claimedJob.TransactionId,
                                false,
                                "Error en procesamiento de cola.",
                                [],
                                null),
                            stoppingToken);
                    }
                    catch (Exception callbackEx)
                    {
                        logger.LogWarning(callbackEx, "No se pudo enviar responseQueueCallback para transactionId {TransactionId}", claimedJob.TransactionId);
                    }
                }
            }
        }
    }
}
