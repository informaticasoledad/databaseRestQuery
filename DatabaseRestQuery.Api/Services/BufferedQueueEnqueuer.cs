using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace DatabaseRestQuery.Api.Services;

public sealed class BufferedQueueEnqueuer(
    IQueueRepository queueRepository,
    IOptions<QueueOptions> options,
    ILogger<BufferedQueueEnqueuer> logger) : BackgroundService, IQueueEnqueuer
{
    private readonly QueueOptions _options = options.Value;
    private readonly Channel<QueryRequest> _channel = Channel.CreateBounded<QueryRequest>(new BoundedChannelOptions(Math.Max(100, options.Value.EnqueueBufferCapacity))
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    public async Task EnqueueAsync(QueryRequest request, bool forceImmediate, CancellationToken cancellationToken)
    {
        if (forceImmediate || !_options.EnableBufferedEnqueue)
        {
            await queueRepository.EnqueueAsync(request, cancellationToken);
            return;
        }

        await _channel.Writer.WriteAsync(request, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var flushInterval = TimeSpan.FromMilliseconds(Math.Max(50, _options.EnqueueFlushIntervalMs));
        var batchSize = Math.Max(1, _options.EnqueueFlushBatchSize);
        var batch = new List<QueryRequest>(batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var hasItem = await _channel.Reader.WaitToReadAsync(stoppingToken);
                if (!hasItem)
                {
                    await Task.Delay(flushInterval, stoppingToken);
                    continue;
                }

                batch.Clear();
                while (batch.Count < batchSize && _channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                foreach (var request in batch)
                {
                    try
                    {
                        await queueRepository.EnqueueAsync(request, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error persistiendo enqueue buffered para transactionId {TransactionId}", request.TransactionId);
                    }
                }

                await Task.Delay(flushInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error en loop de BufferedQueueEnqueuer");
                await Task.Delay(flushInterval, stoppingToken);
            }
        }
    }
}
