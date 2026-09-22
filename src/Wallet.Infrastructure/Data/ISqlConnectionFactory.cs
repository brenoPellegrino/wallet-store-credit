using Microsoft.Data.SqlClient;

namespace Wallet.Infrastructure.Data;

/// <summary>
/// Creates SQL Server connections for the raw ADO.NET data access paths.
/// A single place to build connections keeps the money code focused on commands and transactions.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>The configured connection string. The migrator uses it to reach the server's master database.</summary>
    string ConnectionString { get; }

    /// <summary>Creates a new, unopened <see cref="SqlConnection"/>.</summary>
    SqlConnection Create();

    /// <summary>Creates and opens a new <see cref="SqlConnection"/>.</summary>
    Task<SqlConnection> CreateOpenAsync(CancellationToken cancellationToken = default);
}
