using System.Data.Common;

namespace DatabaseRestQuery.Api.Services;

public interface IDbConnectionFactory
{
    Task<DbConnectionLease> RentOpenAsync(string serverType, string connectionString, CancellationToken cancellationToken);
}
