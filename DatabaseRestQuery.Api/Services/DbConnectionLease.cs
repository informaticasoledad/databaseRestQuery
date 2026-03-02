using System.Data.Common;
using System.Threading;

namespace DatabaseRestQuery.Api.Services;

public sealed class DbConnectionLease : IAsyncDisposable
{
    private readonly Func<DbConnection, ValueTask> _releaseAsync;
    private int _released;

    public DbConnectionLease(DbConnection connection, Func<DbConnection, ValueTask> releaseAsync)
    {
        Connection = connection;
        _releaseAsync = releaseAsync;
    }

    public DbConnection Connection { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        await _releaseAsync(Connection);
    }
}
