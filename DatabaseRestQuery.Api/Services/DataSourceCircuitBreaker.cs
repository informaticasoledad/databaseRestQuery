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

        var now = DateTime.UtcNow;
        if (state.OpenUntilUtc.HasValue && state.OpenUntilUtc.Value > now)
        {
            throw new InvalidOperationException($"Circuit breaker abierto para datasource ({key}). Reintente mas tarde.");
        }

        // El periodo abierto expiro: reseteamos estado para permitir trafico nuevamente.
        _states.TryRemove(key, out _);
    }

    public void RegisterFailure(string key)
    {
        if (!_options.EnableCircuitBreaker)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var failureWindowSeconds = Math.Max(1, _options.CircuitBreakerFailureWindowSeconds);
        var failureWindow = TimeSpan.FromSeconds(failureWindowSeconds);

        _states.AddOrUpdate(key,
            _ =>
            {
                var failures = 1;
                return failures >= _options.CircuitBreakerFailureThreshold
                    ? new CircuitState(failures, now, now.AddSeconds(Math.Max(1, _options.CircuitBreakerOpenSeconds)))
                    : new CircuitState(failures, now, null);
            },
            (_, previous) =>
            {
                var shouldResetFailures = now - previous.LastFailureUtc > failureWindow;
                var failures = shouldResetFailures ? 1 : previous.Failures + 1;
                return failures >= _options.CircuitBreakerFailureThreshold
                    ? new CircuitState(failures, now, now.AddSeconds(Math.Max(1, _options.CircuitBreakerOpenSeconds)))
                    : new CircuitState(failures, now, null);
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

    private sealed record CircuitState(int Failures, DateTime LastFailureUtc, DateTime? OpenUntilUtc);
}
