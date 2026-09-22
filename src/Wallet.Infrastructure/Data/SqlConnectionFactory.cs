using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Wallet.Infrastructure.Configuration;

namespace Wallet.Infrastructure.Data;

/// <inheritdoc />
public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private readonly string _connectionString;

    public SqlConnectionFactory(IOptions<WalletDatabaseOptions> options)
    {
        var value = options.Value.ConnectionString;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Missing connection string. Set {WalletDatabaseOptions.SectionName}:ConnectionString in configuration.");
        }

        _connectionString = value;
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
