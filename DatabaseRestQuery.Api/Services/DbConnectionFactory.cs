using DatabaseRestQuery.Api.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Diagnostics;

namespace DatabaseRestQuery.Api.Services;

public sealed class DbConnectionFactory(
    IOptions<DbConnectionPoolOptions> options,
    IAppMetrics metrics) : IDbConnectionFactory, IAsyncDisposable
{
    private readonly int _maxConnectionsPerDataSource = Math.Max(1, options.Value.MaxConnectionsPerDataSource);
    private readonly TimeSpan _idleConnectionTtl = TimeSpan.FromSeconds(Math.Max(1, options.Value.IdleConnectionTtlSeconds));
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromSeconds(Math.Max(5, options.Value.CleanupIntervalSeconds));
    private readonly bool _resetSessionOnReturn = options.Value.ResetSessionOnReturn;
    private readonly int _resetCommandTimeoutSeconds = Math.Max(1, options.Value.ResetCommandTimeoutSeconds);
    private readonly ConcurrentDictionary<string, ConnectionBucket> _buckets = new(StringComparer.Ordinal);
    private readonly IAppMetrics _metrics = metrics;

    private int _cleanupRunning;
    private int _disposed;
    private long _nextCleanupTicks = DateTime.UtcNow.AddSeconds(5).Ticks;

    public async Task<DbConnectionLease> RentOpenAsync(string serverType, string connectionString, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(serverType))
        {
            throw new ArgumentException("El tipo de servidor es obligatorio.", nameof(serverType));
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("La cadena de conexion es obligatoria.", nameof(connectionString));
        }

        var normalizedType = NormalizeServerType(serverType);
        var key = $"{normalizedType}:{connectionString}";

        TryScheduleCleanup();

        while (true)
        {
            var bucket = _buckets.GetOrAdd(
                key,
                _ => new ConnectionBucket(
                    () => CreateConnection(normalizedType, connectionString),
                    _maxConnectionsPerDataSource,
                    _idleConnectionTtl,
                    ResetSessionStateAsync,
                    _metrics));

            var waitStopwatch = Stopwatch.StartNew();

            try
            {
                var rent = await bucket.RentAsync(cancellationToken);
                _metrics.RecordDbPoolRent(rent.Reused, waitStopwatch.Elapsed.TotalMilliseconds);
                PublishPoolState();
                return new DbConnectionLease(rent.Connection, connection => ReturnAsync(bucket, connection));
            }
            catch (OperationCanceledException)
            {
                _metrics.IncrementDbPoolRentCanceled();
                throw;
            }
            catch (ObjectDisposedException) when (Volatile.Read(ref _disposed) == 0)
            {
                // El bucket pudo ser evictado entre operaciones concurrentes; reintentamos con uno nuevo.
            }
        }
    }

    private async ValueTask ReturnAsync(ConnectionBucket bucket, DbConnection connection)
    {
        await bucket.ReturnAsync(connection);
        PublishPoolState();
        TryScheduleCleanup();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var entry in _buckets.ToArray())
        {
            if (_buckets.TryRemove(entry.Key, out var bucket))
            {
                await bucket.DisposeAsync("factory_dispose");
            }
        }

        PublishPoolState();
    }

    private static string NormalizeServerType(string serverType)
    {
        return serverType
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static DbConnection CreateConnection(string normalizedType, string connectionString)
    {
        return normalizedType switch
        {
            "sqlserver" => new SqlConnection(connectionString),
            "sqlserverlegacy" => new OdbcConnection(connectionString),
            "freetds" => new OdbcConnection(connectionString),
            "postgresql" => new NpgsqlConnection(connectionString),
            "mysql" => new MySqlConnection(connectionString),
            "db2iseries" => new OdbcConnection(connectionString),
            _ => throw new NotSupportedException($"Tipo de servidor no soportado: {normalizedType}")
        };
    }

    private async ValueTask<bool> ResetSessionStateAsync(DbConnection connection)
    {
        if (!_resetSessionOnReturn)
        {
            return true;
        }

        try
        {
            switch (connection)
            {
                case MySqlConnection mysqlConnection:
                    await mysqlConnection.ResetConnectionAsync();
                    return true;
                case NpgsqlConnection:
                    await ExecuteResetCommandAsync(connection, "DISCARD ALL;");
                    return true;
                case SqlConnection:
                    await ExecuteResetCommandAsync(connection, "EXEC sp_reset_connection;");
                    return true;
                default:
                    return true;
            }
        }
        catch
        {
            return false;
        }
    }

    private async Task ExecuteResetCommandAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.Text;
        command.CommandText = sql;
        command.CommandTimeout = _resetCommandTimeoutSeconds;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private void TryScheduleCleanup()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks < Volatile.Read(ref _nextCleanupTicks))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) != 0)
        {
            return;
        }

        Volatile.Write(ref _nextCleanupTicks, DateTime.UtcNow.Add(_cleanupInterval).Ticks);

        _ = Task.Run(async () =>
        {
            try
            {
                await CleanupBucketsAsync();
            }
            finally
            {
                Interlocked.Exchange(ref _cleanupRunning, 0);
            }
        });
    }

    private async Task CleanupBucketsAsync()
    {
        var utcNow = DateTime.UtcNow;

        foreach (var entry in _buckets.ToArray())
        {
            var bucket = entry.Value;
            await bucket.CleanupExpiredIdleAsync(utcNow);

            if (!bucket.IsEvictionCandidate(utcNow, _idleConnectionTtl))
            {
                continue;
            }

            if (!_buckets.TryRemove(entry.Key, out var removed))
            {
                continue;
            }

            _metrics.IncrementDbPoolBucketEvicted();
            await removed.DisposeAsync("bucket_evicted");
        }

        PublishPoolState();
    }

    private void PublishPoolState()
    {
        var bucketCount = 0;
        var created = 0;
        var idle = 0;

        foreach (var bucket in _buckets.Values)
        {
            bucketCount++;
            created += bucket.CreatedCount;
            idle += bucket.IdleCount;
        }

        _metrics.SetDbPoolState(bucketCount, created, idle);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(DbConnectionFactory));
        }
    }

    private sealed class ConnectionBucket(
        Func<DbConnection> connectionFactory,
        int maxSize,
        TimeSpan idleConnectionTtl,
        Func<DbConnection, ValueTask<bool>> resetSessionAsync,
        IAppMetrics metrics) : IAsyncDisposable
    {
        private readonly ConcurrentQueue<IdleConnectionEntry> _idle = new();
        private readonly SemaphoreSlim _available = new(0);

        private int _created;
        private int _idleCount;
        private int _disposed;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;

        public int CreatedCount => Math.Max(0, Volatile.Read(ref _created));
        public int IdleCount => Math.Max(0, Volatile.Read(ref _idleCount));

        public async Task<RentResult> RentAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                ThrowIfDisposed();

                if (TryTakeIdle(out var idleEntry))
                {
                    if (IsExpired(idleEntry.ReturnedAtTicksUtc, DateTime.UtcNow))
                    {
                        await DisposeConnectionAsync(idleEntry.Connection, "idle_ttl");
                        Interlocked.Decrement(ref _created);
                        continue;
                    }

                    var reopened = await EnsureOpenAsync(idleEntry.Connection, cancellationToken);
                    if (reopened is not null)
                    {
                        Touch();
                        return new RentResult(reopened, true);
                    }

                    Interlocked.Decrement(ref _created);
                    continue;
                }

                if (TryCreateSlot())
                {
                    var newConnection = connectionFactory();
                    var opened = await EnsureOpenAsync(newConnection, cancellationToken);
                    if (opened is not null)
                    {
                        Touch();
                        return new RentResult(opened, false);
                    }

                    Interlocked.Decrement(ref _created);
                    continue;
                }

                try
                {
                    await _available.WaitAsync(cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    ThrowIfDisposed();
                    throw;
                }
            }
        }

        public async ValueTask ReturnAsync(DbConnection connection)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                await DisposeConnectionAsync(connection, "bucket_disposed");
                Interlocked.Decrement(ref _created);
                return;
            }

            if (connection.State != ConnectionState.Open)
            {
                await DisposeConnectionAsync(connection, "closed_on_return");
                Interlocked.Decrement(ref _created);
                return;
            }

            var resetOk = await resetSessionAsync(connection);
            if (!resetOk)
            {
                await DisposeConnectionAsync(connection, "reset_failed");
                Interlocked.Decrement(ref _created);
                return;
            }

            _idle.Enqueue(new IdleConnectionEntry(connection, DateTime.UtcNow.Ticks));
            Interlocked.Increment(ref _idleCount);
            _available.Release();
            Touch();
        }

        public async Task CleanupExpiredIdleAsync(DateTime utcNow)
        {
            while (TryTakeIdle(out var idleEntry))
            {
                if (!IsExpired(idleEntry.ReturnedAtTicksUtc, utcNow))
                {
                    // El primero no expirado indica que el resto tampoco (cola FIFO por return).
                    _idle.Enqueue(idleEntry);
                    Interlocked.Increment(ref _idleCount);
                    _available.Release();
                    break;
                }

                await DisposeConnectionAsync(idleEntry.Connection, "idle_ttl");
                Interlocked.Decrement(ref _created);
            }
        }

        public bool IsEvictionCandidate(DateTime utcNow, TimeSpan ttl)
        {
            if (Volatile.Read(ref _created) != 0)
            {
                return false;
            }

            if (Volatile.Read(ref _idleCount) != 0)
            {
                return false;
            }

            var inactiveFor = utcNow - new DateTime(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);
            return inactiveFor >= ttl;
        }

        public async ValueTask DisposeAsync() => await DisposeAsync("bucket_dispose");

        public async ValueTask DisposeAsync(string reason)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            while (_idle.TryDequeue(out var idleEntry))
            {
                Interlocked.Decrement(ref _idleCount);
                await DisposeConnectionAsync(idleEntry.Connection, reason);
                Interlocked.Decrement(ref _created);
            }

            _available.Dispose();
        }

        private bool TryTakeIdle(out IdleConnectionEntry idleEntry)
        {
            if (!_available.Wait(0))
            {
                idleEntry = default;
                return false;
            }

            if (_idle.TryDequeue(out idleEntry))
            {
                Interlocked.Decrement(ref _idleCount);
                return true;
            }

            idleEntry = default;
            return false;
        }

        private bool TryCreateSlot()
        {
            while (true)
            {
                ThrowIfDisposed();

                var current = Volatile.Read(ref _created);
                if (current >= maxSize)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _created, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        private bool IsExpired(long returnedAtTicksUtc, DateTime utcNow)
        {
            var idleTime = utcNow - new DateTime(returnedAtTicksUtc, DateTimeKind.Utc);
            return idleTime >= idleConnectionTtl;
        }

        private async Task<DbConnection?> EnsureOpenAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                if (connection.State != ConnectionState.Open)
                {
                    await connection.OpenAsync(cancellationToken);
                }

                return connection;
            }
            catch
            {
                await DisposeConnectionAsync(connection, "open_failed");
                return null;
            }
        }

        private async ValueTask DisposeConnectionAsync(DbConnection connection, string reason)
        {
            try
            {
                await connection.DisposeAsync();
            }
            catch
            {
                // Ignoramos errores de dispose para no bloquear la devolucion del lease.
            }
            finally
            {
                metrics.IncrementDbPoolConnectionClosed(reason);
            }
        }

        private void Touch()
        {
            Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(ConnectionBucket));
            }
        }

        private readonly record struct IdleConnectionEntry(DbConnection Connection, long ReturnedAtTicksUtc);
    }

    private readonly record struct RentResult(DbConnection Connection, bool Reused);
}
