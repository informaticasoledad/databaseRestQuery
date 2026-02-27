using DatabaseRestQuery.Api.Models;

namespace DatabaseRestQuery.Api.Services;

public interface IRequestHistoryRepository
{
    Task InitializeAsync();
    Task UpsertRequestAsync(string transactionId, QueryRequest request, string channel, CancellationToken cancellationToken);
    Task UpsertResponseAsync(string transactionId, object responsePayload, bool ok, string message, CancellationToken cancellationToken);
    Task<IReadOnlyList<RequestHistoryItem>> GetRecentAsync(int limit, CancellationToken cancellationToken);
}
