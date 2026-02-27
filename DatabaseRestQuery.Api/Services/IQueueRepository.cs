using DatabaseRestQuery.Api.Models;

namespace DatabaseRestQuery.Api.Services;

public interface IQueueRepository
{
    Task InitializeAsync();
    Task EnqueueAsync(QueryRequest request, CancellationToken cancellationToken);
    Task<QueueClaimedJob?> ClaimNextPendingAsync(string workerId, int workerIndex, int workerCount, CancellationToken cancellationToken);
    Task MarkCompletedAsync(string transactionId, string message, IReadOnlyList<Dictionary<string, object?>> result, CancellationToken cancellationToken);
    Task<bool> RetryOrFailAsync(string transactionId, string error, CancellationToken cancellationToken);
    Task<QueueJob?> GetJobAsync(string transactionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingJobItem>> GetPendingJobsAsync(CancellationToken cancellationToken);
    Task<int> PurgePendingAsync(CancellationToken cancellationToken);
    Task<int> CleanupFinishedAsync(CancellationToken cancellationToken);
    Task<QueueStats> GetStatsAsync(CancellationToken cancellationToken);
}
