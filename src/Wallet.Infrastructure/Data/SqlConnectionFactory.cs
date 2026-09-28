using Microsoft.Data.SqlClient;
using Wallet.Infrastructure.Configuration;

namespace Wallet.Infrastructure.Data;

/// <inheritdoc />
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Missing connection string. Set {WalletDatabaseOptions.SectionName}:ConnectionString in configuration.");
        }

        _connectionString = connectionString;
    }

    public string ConnectionString => _connectionString;

    public SqlConnection Create() => new(_connectionString);

    public async Task<SqlConnection> CreateOpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
