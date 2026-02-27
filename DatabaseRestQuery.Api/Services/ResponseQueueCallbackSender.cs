using DatabaseRestQuery.Api.Models;
using System.Net.Http.Json;

namespace DatabaseRestQuery.Api.Services;

public sealed class ResponseQueueCallbackSender(IHttpClientFactory httpClientFactory, ILogger<ResponseQueueCallbackSender> logger) : IResponseQueueCallbackSender
{
    public async Task SendAsync(QueryRequest request, QueryResponse response, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ResponseQueueCallback))
        {
            return;
        }

        if (!Uri.TryCreate(request.ResponseQueueCallback, UriKind.Absolute, out var callbackUri) ||
            (callbackUri.Scheme != Uri.UriSchemeHttp && callbackUri.Scheme != Uri.UriSchemeHttps))
        {
            logger.LogWarning("responseQueueCallback invalido para transactionId {TransactionId}: {Callback}", response.TransactionId, request.ResponseQueueCallback);
            return;
        }

        var payload = new
        {
            transactionId = response.TransactionId,
            responseFormat = request.ResponseFormat,
            response
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, callbackUri)
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.TryAddWithoutValidation("X-Transaction-Id", response.TransactionId);

        using var client = httpClientFactory.CreateClient("response-queue-callback");
        using var httpResponse = await client.SendAsync(httpRequest, cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            var body = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning("Callback devolvio status {StatusCode} para transactionId {TransactionId}. Body: {Body}", (int)httpResponse.StatusCode, response.TransactionId, body);
        }
    }
}
