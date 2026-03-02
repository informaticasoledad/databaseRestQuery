namespace DatabaseRestQuery.Api.Options;

public sealed class DbConnectionPoolOptions
{
    public const string SectionName = "DbConnectionPool";
    public int MaxConnectionsPerDataSource { get; set; } = 16;
    public int IdleConnectionTtlSeconds { get; set; } = 300;
    public int CleanupIntervalSeconds { get; set; } = 60;
    public bool ResetSessionOnReturn { get; set; } = true;
    public int ResetCommandTimeoutSeconds { get; set; } = 5;
}
