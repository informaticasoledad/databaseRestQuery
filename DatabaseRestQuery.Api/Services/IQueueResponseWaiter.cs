using DatabaseRestQuery.Api.Models;

namespace DatabaseRestQuery.Api.Services;

public interface IQueueResponseWaiter
{
    Task<QueueJob?> WaitForCompletionAsync(string transactionId, TimeSpan timeout, CancellationToken cancellationToken);
}
