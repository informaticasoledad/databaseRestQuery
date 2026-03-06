using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DatabaseRestQuery.Api.Services;

public sealed class SqliteQueueRepository(IOptions<QueueOptions> options, IHostEnvironment environment) : IQueueRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly QueueOptions _options = options.Value;
    private readonly IHostEnvironment _environment = environment;

    public async Task InitializeAsync()
    {
        var fullPath = GetFullDbPath();
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await ConfigureSqliteAsync(connection);

        const string createSql = """
            CREATE TABLE IF NOT EXISTS queue_jobs (
                transaction_id TEXT PRIMARY KEY,
                request_json TEXT NOT NULL,
                request_hash TEXT NULL,
                partition_key TEXT NOT NULL DEFAULT 'default',
                status TEXT NOT NULL,
                message TEXT NULL,
                result_json TEXT NULL,
                error TEXT NULL,
                worker_id TEXT NULL,
                attempts INTEGER NOT NULL DEFAULT 0,
                max_attempts INTEGER NOT NULL DEFAULT 3,
                next_attempt_at TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_queue_jobs_status_created
            ON queue_jobs(status, created_at);
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = createSql;
        await command.ExecuteNonQueryAsync();

        await EnsureColumnAsync(connection, "attempts", "INTEGER NOT NULL DEFAULT 0");
        await EnsureColumnAsync(connection, "max_attempts", "INTEGER NOT NULL DEFAULT 3");
        await EnsureColumnAsync(connection, "next_attempt_at", "TEXT NULL");
        await EnsureColumnAsync(connection, "partition_key", "TEXT NOT NULL DEFAULT 'default'");
        await EnsureColumnAsync(connection, "request_hash", "TEXT NULL");
        await EnsureColumnAsync(connection, "export_json", "TEXT NULL");

        await using var createRetryIndexCommand = connection.CreateCommand();
        createRetryIndexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_queue_jobs_next_attempt
            ON queue_jobs(status, next_attempt_at);
            """;
        await createRetryIndexCommand.ExecuteNonQueryAsync();

        await using var createPartitionIndexCommand = connection.CreateCommand();
        createPartitionIndexCommand.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_queue_jobs_partition_status
            ON queue_jobs(partition_key, status, created_at);
            """;
        await createPartitionIndexCommand.ExecuteNonQueryAsync();
    }

    public async Task EnqueueAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.TransactionId))
        {
            throw new ArgumentException("transactionId es obligatorio para encolar.", nameof(request));
        }

        var now = DateTime.UtcNow;
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var requestHash = ComputeRequestHash(request);
        var partitionKey = BuildPartitionKey(request);
        var maxAttempts = Math.Max(1, _options.MaxRetries);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await PurgeExpiredResponsesAsync(connection, cancellationToken);
        await EnforceResponseCapacityAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO queue_jobs (
                transaction_id, request_json, request_hash, partition_key, status, message, created_at, updated_at, attempts, max_attempts, next_attempt_at
            ) VALUES (
                $transactionId, $requestJson, $requestHash, $partitionKey, 'Pending', 'Peticion encolada.', $createdAt, $updatedAt, 0, $maxAttempts, $nextAttemptAt
            );
            """;
        command.Parameters.AddWithValue("$transactionId", request.TransactionId);
        command.Parameters.AddWithValue("$requestJson", requestJson);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$partitionKey", partitionKey);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$maxAttempts", maxAttempts);
        command.Parameters.AddWithValue("$nextAttemptAt", now.ToString("O"));

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            var existingHash = await GetExistingRequestHashAsync(connection, request.TransactionId!, cancellationToken);
            if (existingHash is not null && !string.Equals(existingHash, requestHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("transactionId duplicado con payload diferente.");
            }

            throw new InvalidOperationException("Ya existe una peticion con ese transactionId.");
        }
    }

    public async Task<QueueClaimedJob?> ClaimNextPendingAsync(string workerId, int workerIndex, int workerCount, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var staleBefore = now.AddSeconds(-Math.Max(10, _options.ProcessingLeaseSeconds));
        var applySharding = _options.EnablePartitionSharding && workerCount > 1;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = applySharding
            ? """
            UPDATE queue_jobs
            SET status = 'Processing', worker_id = $workerId, updated_at = $updatedAt
            WHERE transaction_id = (
                SELECT transaction_id
                FROM queue_jobs
                WHERE (
                    (
                        status = 'Pending'
                        AND (next_attempt_at IS NULL OR next_attempt_at <= $now)
                    )
                    OR (
                        status = 'Processing'
                        AND updated_at <= $staleBefore
                    )
                )
                AND (
                    (ifnull(unicode(substr(partition_key, 1, 1)), 0)
                    + ifnull(unicode(substr(partition_key, 2, 1)), 0)
                    + ifnull(unicode(substr(partition_key, 3, 1)), 0)) % $workerCount
                ) = $workerIndex
                ORDER BY created_at
                LIMIT 1
            )
            RETURNING transaction_id, request_json;
            """
            : """
            UPDATE queue_jobs
            SET status = 'Processing', worker_id = $workerId, updated_at = $updatedAt
            WHERE transaction_id = (
                SELECT transaction_id
                FROM queue_jobs
                WHERE (
                    status = 'Pending'
                    AND (next_attempt_at IS NULL OR next_attempt_at <= $now)
                )
                OR (
                    status = 'Processing'
                    AND updated_at <= $staleBefore
                )
                ORDER BY created_at
                LIMIT 1
            )
            RETURNING transaction_id, request_json;
            """;

        command.Parameters.AddWithValue("$workerId", workerId);
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$staleBefore", staleBefore.ToString("O"));
        command.Parameters.AddWithValue("$workerCount", Math.Max(1, workerCount));
        command.Parameters.AddWithValue("$workerIndex", Math.Max(0, workerIndex));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var transactionId = reader.GetString(0);
        var requestJson = reader.GetString(1);
        var request = JsonSerializer.Deserialize<QueryRequest>(requestJson, JsonOptions)
            ?? throw new InvalidOperationException($"No se pudo deserializar el request para {transactionId}");

        return new QueueClaimedJob(transactionId, request);
    }

    public async Task MarkCompletedAsync(string transactionId, string message, IReadOnlyList<Dictionary<string, object?>> result, QueryExportInfo? export, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var resultJson = JsonSerializer.Serialize(result, JsonOptions);
        var exportJson = export is null ? null : JsonSerializer.Serialize(export, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queue_jobs
            SET status = 'Completed', message = $message, result_json = $resultJson, export_json = $exportJson, error = NULL, worker_id = NULL, next_attempt_at = NULL, updated_at = $updatedAt
            WHERE transaction_id = $transactionId;
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$resultJson", resultJson);
        command.Parameters.AddWithValue("$exportJson", (object?)exportJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> RetryOrFailAsync(string transactionId, string error, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var nextAttemptAt = now.AddSeconds(Math.Max(1, _options.RetryDelaySeconds));

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE queue_jobs
            SET
                attempts = attempts + 1,
                status = CASE WHEN attempts + 1 < max_attempts THEN 'Pending' ELSE 'Failed' END,
                message = CASE
                    WHEN attempts + 1 < max_attempts THEN 'Reintentando por error de ejecucion.'
                    ELSE 'Error en procesamiento de cola.'
                END,
                error = $error,
                worker_id = NULL,
                next_attempt_at = CASE
                    WHEN attempts + 1 < max_attempts THEN $nextAttemptAt
                    ELSE NULL
                END,
                updated_at = $updatedAt
            WHERE transaction_id = $transactionId
            RETURNING status;
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$error", error);
        command.Parameters.AddWithValue("$nextAttemptAt", nextAttemptAt.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        var status = reader.GetString(0);
        return string.Equals(status, "Pending", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<QueueJob?> GetJobAsync(string transactionId, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transaction_id, status, message, result_json, error, attempts, max_attempts, next_attempt_at, partition_key, created_at, updated_at, worker_id, request_json, export_json
            FROM queue_jobs
            WHERE transaction_id = $transactionId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var resultJson = reader.IsDBNull(3) ? null : reader.GetString(3);
        var result = string.IsNullOrWhiteSpace(resultJson)
            ? null
            : JsonSerializer.Deserialize<IReadOnlyList<Dictionary<string, object?>>>(resultJson, JsonOptions);

        var compressResult = false;
        var responseFormat = "json";
        if (!reader.IsDBNull(12))
        {
            var requestJson = reader.GetString(12);
            var parsedRequest = JsonSerializer.Deserialize<QueryRequest>(requestJson, JsonOptions);
            compressResult = parsedRequest?.CompressResult ?? false;
            responseFormat = parsedRequest?.ResponseFormat ?? "json";
        }

        QueryExportInfo? export = null;
        if (!reader.IsDBNull(13))
        {
            var exportJson = reader.GetString(13);
            export = JsonSerializer.Deserialize<QueryExportInfo>(exportJson, JsonOptions);
        }

        return new QueueJob(
            reader.GetString(0),
            ParseStatus(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            result,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
            reader.IsDBNull(6) ? 1 : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : ParseUtc(reader.GetString(7)),
            compressResult,
            responseFormat,
            reader.IsDBNull(8) ? "default" : reader.GetString(8),
            ParseUtc(reader.GetString(9)),
            ParseUtc(reader.GetString(10)),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            export
        );
    }

    public async Task<IReadOnlyList<PendingJobItem>> GetPendingJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = new List<PendingJobItem>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transaction_id, status, attempts, max_attempts, next_attempt_at, partition_key, created_at, updated_at
            FROM queue_jobs
            WHERE status IN ('Pending', 'Processing')
            ORDER BY created_at;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(new PendingJobItem(
                reader.GetString(0),
                ParseStatus(reader.GetString(1)),
                reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                reader.IsDBNull(3) ? 1 : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
                reader.IsDBNull(5) ? "default" : reader.GetString(5),
                ParseUtc(reader.GetString(6)),
                ParseUtc(reader.GetString(7))
            ));
        }

        return jobs;
    }

    public async Task<int> PurgePendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM queue_jobs WHERE status = 'Pending';";
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<int> CleanupFinishedAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await OptimizeSqliteAsync(connection, cancellationToken);

        var deletedByRetention = await PurgeExpiredResponsesAsync(connection, cancellationToken);
        var deletedByCapacity = await EnforceResponseCapacityAsync(connection, cancellationToken);
        return deletedByRetention + deletedByCapacity;
    }

    public async Task<QueueStats> GetStatsAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN status = 'Pending' THEN 1 ELSE 0 END) AS pending,
                SUM(CASE WHEN status = 'Processing' THEN 1 ELSE 0 END) AS processing,
                SUM(CASE WHEN status = 'Completed' THEN 1 ELSE 0 END) AS completed,
                SUM(CASE WHEN status = 'Failed' THEN 1 ELSE 0 END) AS failed,
                SUM(CASE WHEN status = 'Pending' AND next_attempt_at > $now THEN 1 ELSE 0 END) AS delayed_retry
            FROM queue_jobs;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return new QueueStats(
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
            reader.IsDBNull(4) ? 0 : reader.GetInt32(4)
        );
    }

    private SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = GetFullDbPath(),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();

        return new SqliteConnection(connectionString);
    }

    private async Task ConfigureSqliteAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA auto_vacuum = INCREMENTAL;
            PRAGMA busy_timeout = 5000;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task OptimizeSqliteAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA optimize;
            PRAGMA incremental_vacuum(1000);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> PurgeExpiredResponsesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var effectiveRetentionMinutes = Math.Max(1, _options.CompletedRetentionMinutes);
        var cutoff = DateTime.UtcNow.AddMinutes(-effectiveRetentionMinutes);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM queue_jobs
            WHERE status IN ('Completed', 'Failed')
              AND updated_at < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<int> EnforceResponseCapacityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var maxItems = Math.Max(1, _options.ResponseQueueMaxItems);
        var targetAfterPurge = maxItems == 1
            ? 0
            : Math.Clamp(_options.ResponseQueueTargetItemsAfterPurge, 1, maxItems - 1);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
            SELECT COUNT(1)
            FROM queue_jobs
            WHERE status IN ('Completed', 'Failed');
            """;
        var currentCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (currentCount < maxItems)
        {
            return 0;
        }

        var toDelete = Math.Max(1, (currentCount - targetAfterPurge) + 1);
        await using var deleteCommand = connection.CreateCommand();
        deleteCommand.CommandText = """
            DELETE FROM queue_jobs
            WHERE transaction_id IN (
                SELECT transaction_id
                FROM queue_jobs
                WHERE status IN ('Completed', 'Failed')
                ORDER BY updated_at
                LIMIT $toDelete
            );
            """;
        deleteCommand.Parameters.AddWithValue("$toDelete", toDelete);
        return await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureColumnAsync(SqliteConnection connection, string columnName, string columnDefinition)
    {
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA table_info(queue_jobs);";

        var exists = false;
        await using (var reader = await checkCommand.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }
        }

        if (exists)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE queue_jobs ADD COLUMN {columnName} {columnDefinition};";
        await alterCommand.ExecuteNonQueryAsync();
    }

    private string GetFullDbPath()
    {
        return Path.IsPathRooted(_options.DbPath)
            ? _options.DbPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, _options.DbPath));
    }

    private static DateTime ParseUtc(string value)
    {
        return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
    }

    private static QueueJobStatus ParseStatus(string status)
    {
        return status.ToLowerInvariant() switch
        {
            "pending" => QueueJobStatus.Pending,
            "processing" => QueueJobStatus.Processing,
            "completed" => QueueJobStatus.Completed,
            "failed" => QueueJobStatus.Failed,
            _ => throw new InvalidOperationException($"Estado de cola desconocido: {status}")
        };
    }

    private static string ComputeRequestHash(QueryRequest request)
    {
        using var sha = SHA256.Create();
        var canonical = request with { TransactionId = null };
        var json = JsonSerializer.Serialize(canonical, JsonOptions);
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }

    private static string BuildPartitionKey(QueryRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.QueuePartition))
        {
            return request.QueuePartition.Trim().ToLowerInvariant();
        }

        if (request.Server is null)
        {
            return "default";
        }

        var normalizedServerType = request.Server.Type.Trim().ToLowerInvariant();
        using var sha = SHA256.Create();
        var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(request.Server.Connstr))).Substring(0, 8).ToLowerInvariant();
        return $"{normalizedServerType}:{hash}";
    }

    private static async Task<string?> GetExistingRequestHashAsync(SqliteConnection connection, string transactionId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT request_hash
            FROM queue_jobs
            WHERE transaction_id = $transactionId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value?.ToString();
    }
}
