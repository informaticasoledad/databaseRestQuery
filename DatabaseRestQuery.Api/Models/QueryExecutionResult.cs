namespace DatabaseRestQuery.Api.Models;

public sealed record QueryExecutionResult(
    bool Ok,
    string Message,
    IReadOnlyList<Dictionary<string, object?>> Result
);
