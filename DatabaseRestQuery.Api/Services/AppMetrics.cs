using Prometheus;

namespace DatabaseRestQuery.Api.Services;

public sealed class AppMetrics : IAppMetrics
{
    private static readonly Counter DbExecutions = Metrics
        .CreateCounter("db_executions_total", "Total de ejecuciones SQL", new CounterConfiguration
        {
            LabelNames = ["server_type", "ok"]
        });

    private static readonly Histogram DbDurationMs = Metrics
        .CreateHistogram("db_execution_duration_ms", "Duracion de ejecuciones SQL (ms)", new HistogramConfiguration
        {
            LabelNames = ["server_type", "ok"]
        });

    private static readonly Counter Enqueues = Metrics
        .CreateCounter("queue_enqueued_total", "Total de peticiones encoladas", new CounterConfiguration
        {
            LabelNames = ["mode"]
        });

    private static readonly Counter BackpressureRejects = Metrics
        .CreateCounter("queue_backpressure_reject_total", "Total de rechazos por backpressure");

    private static readonly Counter CircuitOpenRejects = Metrics
        .CreateCounter("db_circuit_open_reject_total", "Total de rechazos por circuit breaker abierto");

    private static readonly Counter Retries = Metrics
        .CreateCounter("queue_retries_total", "Total de reintentos de cola");

    public void RecordDbExecution(string serverType, bool ok, double durationMs)
    {
        var okLabel = ok ? "true" : "false";
        DbExecutions.WithLabels(serverType, okLabel).Inc();
        DbDurationMs.WithLabels(serverType, okLabel).Observe(durationMs);
    }

    public void IncrementEnqueued(string mode) => Enqueues.WithLabels(mode).Inc();

    public void IncrementBackpressureReject() => BackpressureRejects.Inc();

    public void IncrementCircuitOpenReject() => CircuitOpenRejects.Inc();

    public void IncrementRetries() => Retries.Inc();
}
