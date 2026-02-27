using System.Data.Common;

namespace DatabaseRestQuery.Api.Services;

public interface IDbConnectionFactory
{
    DbConnection Create(string serverType, string connectionString);
}
