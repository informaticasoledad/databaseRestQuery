using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using Microsoft.Extensions.Options;

namespace DatabaseRestQuery.Api.Services;

public sealed class QueueResponseWaiter(IQueueRepository queueRepository, IOptions<QueueOptions> options) : IQueueResponseWaiter
{
    private readonly int _pollIntervalMs = Math.Max(100, options.Value.WaitPollIntervalMs);

    public async Task<QueueJob?> WaitForCompletionAsync(string transactionId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            var job = await queueRepository.GetJobAsync(transactionId, timeoutCts.Token);
            if (job is null)
            {
                return null;
            }

            if (job.Status is QueueJobStatus.Completed or QueueJobStatus.Failed)
            {
                return job;
            }

            await Task.Delay(_pollIntervalMs, timeoutCts.Token);
        }

        return null;
    }
}
