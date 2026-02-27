using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using System.Data.Common;
using System.Data.Odbc;

namespace DatabaseRestQuery.Api.Services;

public sealed class DbConnectionFactory : IDbConnectionFactory
{
    public DbConnection Create(string serverType, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(serverType))
        {
            throw new ArgumentException("El tipo de servidor es obligatorio.", nameof(serverType));
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("La cadena de conexion es obligatoria.", nameof(connectionString));
        }

        var normalizedType = NormalizeServerType(serverType);

        return normalizedType switch
        {
            "sqlserver" => new SqlConnection(connectionString),
            "sqlserverlegacy" => new OdbcConnection(connectionString),
            "freetds" => new OdbcConnection(connectionString),
            "postgresql" => new NpgsqlConnection(connectionString),
            "mysql" => new MySqlConnection(connectionString),
            "db2iseries" => new OdbcConnection(connectionString),
            _ => throw new NotSupportedException($"Tipo de servidor no soportado: {serverType}")
        };
    }

    private static string NormalizeServerType(string serverType)
    {
        return serverType
            .Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }
}
