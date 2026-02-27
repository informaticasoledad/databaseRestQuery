using DatabaseRestQuery.Api.Models;

namespace DatabaseRestQuery.Api.Services;

public interface IQueueEnqueuer
{
    Task EnqueueAsync(QueryRequest request, bool forceImmediate, CancellationToken cancellationToken);
}
