namespace DatabaseRestQuery.Api.Services;

public interface IAppMetrics
{
    void RecordDbExecution(string serverType, bool ok, double durationMs);
    void IncrementEnqueued(string mode);
    void IncrementBackpressureReject();
    void IncrementCircuitOpenReject();
    void IncrementRetries();
}
