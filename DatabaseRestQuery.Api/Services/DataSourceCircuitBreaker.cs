using DatabaseRestQuery.Api.Options;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

namespace DatabaseRestQuery.Api.Services;

public sealed class DataSourceCircuitBreaker(IOptions<QueueOptions> options) : IDataSourceCircuitBreaker
{
    private readonly QueueOptions _options = options.Value;
    private readonly ConcurrentDictionary<string, CircuitState> _states = new(StringComparer.OrdinalIgnoreCase);

    public void ThrowIfOpen(string key)
    {
        if (!_options.EnableCircuitBreaker)
        {
            return;
        }

        if (!_states.TryGetValue(key, out var state))
        {
            return;
        }

        if (state.OpenUntilUtc.HasValue && state.OpenUntilUtc.Value > DateTime.UtcNow)
        {
            throw new InvalidOperationException($"Circuit breaker abierto para datasource ({key}). Reintente mas tarde.");
        }
    }

    public void RegisterFailure(string key)
    {
        if (!_options.EnableCircuitBreaker)
        {
            return;
        }

        _states.AddOrUpdate(key,
            _ =>
            {
                var failures = 1;
                return failures >= _options.CircuitBreakerFailureThreshold
                    ? new CircuitState(failures, DateTime.UtcNow.AddSeconds(Math.Max(1, _options.CircuitBreakerOpenSeconds)))
                    : new CircuitState(failures, null);
            },
            (_, previous) =>
            {
                var failures = previous.Failures + 1;
                return failures >= _options.CircuitBreakerFailureThreshold
                    ? new CircuitState(failures, DateTime.UtcNow.AddSeconds(Math.Max(1, _options.CircuitBreakerOpenSeconds)))
                    : new CircuitState(failures, previous.OpenUntilUtc);
            });
    }

    public void RegisterSuccess(string key)
    {
        if (!_options.EnableCircuitBreaker)
        {
            return;
        }

        _states.TryRemove(key, out _);
    }

    private sealed record CircuitState(int Failures, DateTime? OpenUntilUtc);
}
