using Microsoft.Data.SqlClient;

namespace Wallet.Infrastructure.Data;

/// <summary>
/// Creates SQL Server connections for the raw ADO.NET data access paths.
/// A single place to build connections keeps the money code focused on commands and transactions.
/// </summary>
public interface ISqlConnectionFactory
{
    /// <summary>Creates a new, unopened <see cref="SqlConnection"/>.</summary>
    SqlConnection Create();

    /// <summary>Creates and opens a new <see cref="SqlConnection"/>.</summary>
    Task<SqlConnection> CreateOpenAsync(CancellationToken cancellationToken = default);
}
