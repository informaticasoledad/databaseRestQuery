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

    private static readonly Counter DbPoolRents = Metrics
        .CreateCounter("db_pool_rent_total", "Total de rent de conexiones del pool", new CounterConfiguration
        {
            LabelNames = ["source"]
        });

    private static readonly Histogram DbPoolRentWaitMs = Metrics
        .CreateHistogram("db_pool_rent_wait_ms", "Tiempo de espera para obtener conexion del pool (ms)");

    private static readonly Counter DbPoolRentCanceled = Metrics
        .CreateCounter("db_pool_rent_canceled_total", "Total de cancelaciones/timeout esperando conexion del pool");

    private static readonly Counter DbPoolConnectionClosed = Metrics
        .CreateCounter("db_pool_connection_closed_total", "Total de conexiones cerradas en el pool", new CounterConfiguration
        {
            LabelNames = ["reason"]
        });

    private static readonly Counter DbPoolBucketEvicted = Metrics
        .CreateCounter("db_pool_bucket_evicted_total", "Total de buckets de pool evictados");

    private static readonly Gauge DbPoolBuckets = Metrics
        .CreateGauge("db_pool_buckets", "Buckets activos de pool (serverType+connstr)");

    private static readonly Gauge DbPoolConnectionsCreated = Metrics
        .CreateGauge("db_pool_connections_created", "Conexiones vivas gestionadas por el pool");

    private static readonly Gauge DbPoolConnectionsIdle = Metrics
        .CreateGauge("db_pool_connections_idle", "Conexiones inactivas actualmente disponibles en el pool");

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

    public void RecordDbPoolRent(bool reused, double waitMs)
    {
        DbPoolRents.WithLabels(reused ? "reused" : "new").Inc();
        DbPoolRentWaitMs.Observe(waitMs);
    }

    public void IncrementDbPoolRentCanceled() => DbPoolRentCanceled.Inc();

    public void IncrementDbPoolConnectionClosed(string reason) => DbPoolConnectionClosed.WithLabels(reason).Inc();

    public void IncrementDbPoolBucketEvicted() => DbPoolBucketEvicted.Inc();

    public void SetDbPoolState(int bucketCount, int createdConnections, int idleConnections)
    {
        DbPoolBuckets.Set(bucketCount);
        DbPoolConnectionsCreated.Set(createdConnections);
        DbPoolConnectionsIdle.Set(idleConnections);
    }
}
