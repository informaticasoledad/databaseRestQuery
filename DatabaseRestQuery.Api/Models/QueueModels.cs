namespace DatabaseRestQuery.Api.Models;

public enum QueueJobStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

public sealed record QueueJob(
    string TransactionId,
    QueueJobStatus Status,
    string? Message,
    IReadOnlyList<Dictionary<string, object?>>? Result,
    string? Error,
    int Attempts,
    int MaxAttempts,
    DateTime? NextAttemptAt,
    bool CompressResult,
    string ResponseFormat,
    string PartitionKey,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? WorkerId,
    QueryExportInfo? Export
);

public sealed record PendingJobItem(
    string TransactionId,
    QueueJobStatus Status,
    int Attempts,
    int MaxAttempts,
    DateTime? NextAttemptAt,
    string PartitionKey,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public sealed record QueueStats(int Pending, int Processing, int Completed, int Failed, int DelayedRetry);

public sealed record QueueClaimedJob(string TransactionId, QueryRequest Request);
