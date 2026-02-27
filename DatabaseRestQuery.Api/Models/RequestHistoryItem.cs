namespace DatabaseRestQuery.Api.Models;

public sealed record RequestHistoryItem(
    string TransactionId,
    string Channel,
    string RequestJson,
    string? ResponseJson,
    bool? Ok,
    string? Message,
    DateTime CreatedAt,
    DateTime UpdatedAt
);
