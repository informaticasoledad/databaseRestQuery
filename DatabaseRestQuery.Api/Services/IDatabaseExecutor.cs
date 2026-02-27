using DatabaseRestQuery.Api.Models;

namespace DatabaseRestQuery.Api.Services;

public interface IDatabaseExecutor
{
    Task<QueryExecutionResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken);
    IAsyncEnumerable<Dictionary<string, object?>> ExecuteStreamAsync(QueryRequest request, CancellationToken cancellationToken);
}
