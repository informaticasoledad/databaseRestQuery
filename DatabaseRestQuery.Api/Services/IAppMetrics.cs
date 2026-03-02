namespace DatabaseRestQuery.Api.Services;

public interface IAppMetrics
{
    void RecordDbExecution(string serverType, bool ok, double durationMs);
    void IncrementEnqueued(string mode);
    void IncrementBackpressureReject();
    void IncrementCircuitOpenReject();
    void IncrementRetries();
    void RecordDbPoolRent(bool reused, double waitMs);
    void IncrementDbPoolRentCanceled();
    void IncrementDbPoolConnectionClosed(string reason);
    void IncrementDbPoolBucketEvicted();
    void SetDbPoolState(int bucketCount, int createdConnections, int idleConnections);
}
