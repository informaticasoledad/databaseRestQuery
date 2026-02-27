using System.Text.Json;

namespace DatabaseRestQuery.Api.Models;

public sealed record QueryRequest(
    ServerRequest? Server = null,
    string? ConnectionName = null,
    string? TransactionId = null,
    string? Query = null,
    CommandRequest? Command = null,
    int ExecutionTimeout = 30,
    int RowsLimit = 0,
    bool WaitForResponse = true,
    bool UseQueue = true,
    bool CompressResult = false,
    bool StreamResult = false,
    string? QueuePartition = null,
    string ResponseFormat = "json",
    string? ResponseQueueCallback = null
);

public sealed record ServerRequest(string Type, string Connstr);

public sealed record CommandRequest(
    int CommandTimeout = 30,
    string? CommandText = null,
    IReadOnlyList<CommandParamRequest>? Params = null
);

public sealed record CommandParamRequest(string Name, JsonElement? Value);
