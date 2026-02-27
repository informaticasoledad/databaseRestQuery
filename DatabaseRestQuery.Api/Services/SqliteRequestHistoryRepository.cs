using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Text.Json;

namespace DatabaseRestQuery.Api.Services;

public sealed class SqliteRequestHistoryRepository(IOptions<QueueOptions> options, IHostEnvironment environment) : IRequestHistoryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly QueueOptions _options = options.Value;
    private readonly IHostEnvironment _environment = environment;

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS api_history (
                transaction_id TEXT PRIMARY KEY,
                channel TEXT NOT NULL,
                request_json TEXT NOT NULL,
                response_json TEXT NULL,
                ok INTEGER NULL,
                message TEXT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_api_history_updated
            ON api_history(updated_at DESC);
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task UpsertRequestAsync(string transactionId, QueryRequest request, string channel, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO api_history (transaction_id, channel, request_json, created_at, updated_at)
            VALUES ($transactionId, $channel, $requestJson, $createdAt, $updatedAt)
            ON CONFLICT(transaction_id) DO UPDATE SET
                channel = excluded.channel,
                request_json = excluded.request_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$channel", channel);
        command.Parameters.AddWithValue("$requestJson", requestJson);
        command.Parameters.AddWithValue("$createdAt", now.ToString("O"));
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertResponseAsync(string transactionId, object responsePayload, bool ok, string message, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var responseJson = JsonSerializer.Serialize(responsePayload, JsonOptions);

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE api_history
            SET response_json = $responseJson,
                ok = $ok,
                message = $message,
                updated_at = $updatedAt
            WHERE transaction_id = $transactionId;
            """;
        command.Parameters.AddWithValue("$transactionId", transactionId);
        command.Parameters.AddWithValue("$responseJson", responseJson);
        command.Parameters.AddWithValue("$ok", ok ? 1 : 0);
        command.Parameters.AddWithValue("$message", message);
        command.Parameters.AddWithValue("$updatedAt", now.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RequestHistoryItem>> GetRecentAsync(int limit, CancellationToken cancellationToken)
    {
        var rows = new List<RequestHistoryItem>();

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT transaction_id, channel, request_json, response_json, ok, message, created_at, updated_at
            FROM api_history
            ORDER BY updated_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RequestHistoryItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4) == 1,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTime.Parse(reader.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(),
                DateTime.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()
            ));
        }

        return rows;
    }

    private SqliteConnection CreateConnection()
    {
        var dbPath = Path.IsPathRooted(_options.DbPath)
            ? _options.DbPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, _options.DbPath));

        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString());
    }
}
