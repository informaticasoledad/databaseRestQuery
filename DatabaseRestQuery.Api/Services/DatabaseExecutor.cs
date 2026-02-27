using DatabaseRestQuery.Api.Infrastructure;
using DatabaseRestQuery.Api.Models;
using DatabaseRestQuery.Api.Options;
using Microsoft.Extensions.Options;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace DatabaseRestQuery.Api.Services;

public sealed class DatabaseExecutor(
    IDbConnectionFactory connectionFactory,
    IDataSourceCircuitBreaker circuitBreaker,
    IAppMetrics metrics,
    IOptions<QueueOptions> options) : IDatabaseExecutor
{
    private static readonly string[] QueryPrefixes = ["select", "with", "show", "describe", "pragma", "explain"];
    private readonly QueueOptions _options = options.Value;

    public async Task<QueryExecutionResult> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var server = request.Server!;
        var upstreamToken = cancellationToken;
        var dataSourceKey = BuildDataSourceKey(server.Type, server.Connstr);
        circuitBreaker.ThrowIfOpen(dataSourceKey);

        var effectiveCommandText = GetEffectiveCommandText(request);
        var commandTimeout = request.Command?.CommandTimeout is > 0 ? request.Command.CommandTimeout : 30;
        var stopwatch = Stopwatch.StartNew();

        using var timeoutCts = request.ExecutionTimeout > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : null;

        if (timeoutCts is not null)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(request.ExecutionTimeout));
            cancellationToken = timeoutCts.Token;
        }

        try
        {
            await using var connection = connectionFactory.Create(server.Type, server.Connstr);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = effectiveCommandText;
            command.CommandType = CommandType.Text;
            command.CommandTimeout = commandTimeout;

            AddParameters(command, request.Command?.Params);
            PrepareIfRequested(command);

            if (!LooksLikeQuery(effectiveCommandText))
            {
                var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
                circuitBreaker.RegisterSuccess(dataSourceKey);
                metrics.RecordDbExecution(server.Type, true, stopwatch.Elapsed.TotalMilliseconds);
                return new QueryExecutionResult(true, $"Comando ejecutado. Filas afectadas: {rowsAffected}", []);
            }

            var result = new List<Dictionary<string, object?>>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rowsLimit = request.RowsLimit > 0 ? request.RowsLimit : int.MaxValue;

            while (await reader.ReadAsync(cancellationToken) && result.Count < rowsLimit)
            {
                result.Add(await ReadCurrentRowAsync(reader, cancellationToken));
            }

            circuitBreaker.RegisterSuccess(dataSourceKey);
            metrics.RecordDbExecution(server.Type, true, stopwatch.Elapsed.TotalMilliseconds);
            return new QueryExecutionResult(true, $"Consulta ejecutada. Filas obtenidas: {result.Count}", result);
        }
        catch (OperationCanceledException) when (timeoutCts is not null && timeoutCts.IsCancellationRequested && !upstreamToken.IsCancellationRequested)
        {
            circuitBreaker.RegisterFailure(dataSourceKey);
            metrics.RecordDbExecution(server.Type, false, stopwatch.Elapsed.TotalMilliseconds);
            throw new TimeoutException("Tiempo de ejecucion excedido.");
        }
        catch
        {
            circuitBreaker.RegisterFailure(dataSourceKey);
            metrics.RecordDbExecution(server.Type, false, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
    }

    public async IAsyncEnumerable<Dictionary<string, object?>> ExecuteStreamAsync(QueryRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var server = request.Server!;
        var dataSourceKey = BuildDataSourceKey(server.Type, server.Connstr);
        circuitBreaker.ThrowIfOpen(dataSourceKey);

        var effectiveCommandText = GetEffectiveCommandText(request);
        var commandTimeout = request.Command?.CommandTimeout is > 0 ? request.Command.CommandTimeout : 30;
        var stopwatch = Stopwatch.StartNew();

        await using var connection = connectionFactory.Create(server.Type, server.Connstr);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = effectiveCommandText;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = commandTimeout;
        AddParameters(command, request.Command?.Params);
        PrepareIfRequested(command);

        if (!LooksLikeQuery(effectiveCommandText))
        {
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
                circuitBreaker.RegisterSuccess(dataSourceKey);
                metrics.RecordDbExecution(server.Type, true, stopwatch.Elapsed.TotalMilliseconds);
                yield break;
            }
            catch
            {
                circuitBreaker.RegisterFailure(dataSourceKey);
                metrics.RecordDbExecution(server.Type, false, stopwatch.Elapsed.TotalMilliseconds);
                throw;
            }
        }

        var rowsLimit = request.RowsLimit > 0 ? request.RowsLimit : int.MaxValue;
        var emitted = 0;

        var succeeded = false;
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (emitted < rowsLimit && await reader.ReadAsync(cancellationToken))
            {
                emitted++;
                yield return await ReadCurrentRowAsync(reader, cancellationToken);
            }

            succeeded = true;
        }
        finally
        {
            if (succeeded)
            {
                circuitBreaker.RegisterSuccess(dataSourceKey);
                metrics.RecordDbExecution(server.Type, true, stopwatch.Elapsed.TotalMilliseconds);
            }
            else
            {
                circuitBreaker.RegisterFailure(dataSourceKey);
                metrics.RecordDbExecution(server.Type, false, stopwatch.Elapsed.TotalMilliseconds);
            }
        }
    }

    private static async Task<Dictionary<string, object?>> ReadCurrentRowAsync(DbDataReader reader, CancellationToken cancellationToken)
    {
        var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = await reader.IsDBNullAsync(i, cancellationToken)
                ? null
                : reader.GetValue(i);
            row[reader.GetName(i)] = value;
        }

        return row;
    }

    private static string GetEffectiveCommandText(QueryRequest request)
    {
        var commandText = request.Command?.CommandText;

        if (!string.IsNullOrWhiteSpace(commandText))
        {
            return commandText;
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            return request.Query;
        }

        throw new ArgumentException("Debe enviar query o command.commandText.");
    }

    private static void ValidateRequest(QueryRequest request)
    {
        if (request.Server is null)
        {
            throw new ArgumentException("El bloque server es obligatorio.");
        }

        if (string.IsNullOrWhiteSpace(request.Server.Type))
        {
            throw new ArgumentException("server.type es obligatorio.");
        }

        if (string.IsNullOrWhiteSpace(request.Server.Connstr))
        {
            throw new ArgumentException("server.connstr es obligatorio.");
        }
    }

    private static void AddParameters(DbCommand command, IReadOnlyList<CommandParamRequest>? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (var param in parameters)
        {
            if (string.IsNullOrWhiteSpace(param.Name))
            {
                continue;
            }

            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = param.Name;
            dbParameter.Value = JsonHelper.JsonElementToObject(param.Value) ?? DBNull.Value;
            command.Parameters.Add(dbParameter);
        }
    }

    private void PrepareIfRequested(DbCommand command)
    {
        if (!_options.EnablePreparedStatements)
        {
            return;
        }

        if (command.Parameters.Count == 0)
        {
            return;
        }

        try
        {
            command.Prepare();
        }
        catch
        {
            // Algunos providers no soportan prepare consistentemente.
        }
    }

    private static bool LooksLikeQuery(string commandText)
    {
        var trimmed = commandText.TrimStart();
        return QueryPrefixes.Any(prefix => trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildDataSourceKey(string serverType, string connstr)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(connstr));
        var hash = Convert.ToHexString(bytes.AsSpan(0, 8));
        return $"{serverType.ToLowerInvariant()}:{hash}";
    }
}
