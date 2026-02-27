namespace DatabaseRestQuery.Api.Options;

public sealed class QueueOptions
{
    public const string SectionName = "Queue";

    public string RunMode { get; set; } = "All";
    public string DbPath { get; set; } = "Data/queue.db";
    public int WorkersCount { get; set; } = 2;
    public int PollIntervalMs { get; set; } = 500;
    public int WaitPollIntervalMs { get; set; } = 500;
    public int BackpressureMaxInFlight { get; set; } = 5000;
    public bool EnablePreparedStatements { get; set; } = true;
    public bool EnableCircuitBreaker { get; set; } = true;
    public int CircuitBreakerFailureThreshold { get; set; } = 5;
    public int CircuitBreakerOpenSeconds { get; set; } = 30;
    public bool EnablePartitionSharding { get; set; } = true;
    public bool EnableBufferedEnqueue { get; set; } = true;
    public int EnqueueBufferCapacity { get; set; } = 5000;
    public int EnqueueFlushIntervalMs { get; set; } = 200;
    public int EnqueueFlushBatchSize { get; set; } = 100;
    public int MaxRetries { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 5;
    public int ProcessingLeaseSeconds { get; set; } = 120;
    public int CleanupIntervalSeconds { get; set; } = 60;
    public int CompletedRetentionHours { get; set; } = 24;
    public int ResponseRetentionHours { get; set; } = 24;
    public int ResponseQueueMaxItems { get; set; } = 10000;
    public int ResponseQueueTargetItemsAfterPurge { get; set; } = 9000;
    public int MaxRowsLimit { get; set; } = 10000;
    public int MaxCommandTextLength { get; set; } = 100000;
    public int MaxParamsCount { get; set; } = 200;
    public int MaxExecutionTimeoutSeconds { get; set; } = 600;
    public int MaxCommandTimeoutSeconds { get; set; } = 600;
}
