using DatabaseRestQuery.Api.Models;

namespace DatabaseRestQuery.Api.Services;

public interface IResponseQueueCallbackSender
{
    Task SendAsync(QueryRequest request, QueryResponse response, CancellationToken cancellationToken);
}
