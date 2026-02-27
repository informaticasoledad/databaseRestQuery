namespace DatabaseRestQuery.Api.Models;

public sealed record QueryResponse(
    string TransactionId,
    bool Ok,
    string Message,
    IReadOnlyList<Dictionary<string, object?>> Result,
    string? CompressedResult,
    QueryExportInfo? Export = null
);
