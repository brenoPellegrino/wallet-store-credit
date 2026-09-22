using System.Data;
using Microsoft.Data.SqlClient;
using Wallet.Core;
using Wallet.Core.Abstractions;
using Wallet.Core.Errors;
using Wallet.Core.Models;

namespace Wallet.Infrastructure.Data;

/// <summary>
/// Raw ADO.NET data access for wallets. Reads are hand-written SQL over
/// <see cref="SqlCommand"/>/<see cref="SqlDataReader"/>; the credit money path calls the
/// <c>usp_CreditWallet</c> stored procedure.
/// </summary>
public sealed class WalletRepository : IWalletRepository
{
    // Thrown by usp_CreditWallet when the wallet is missing or deleted (THROW 50001).
    private const int WalletNotFoundError = 50001;

    private readonly ISqlConnectionFactory _connectionFactory;

    public WalletRepository(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<WalletAccount> CreateWalletAsync(
        string userId,
        string? metadata = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT dbo.wallets (user_id, metadata)
            OUTPUT inserted.public_id, inserted.user_id, inserted.metadata,
                   inserted.created_at_utc, inserted.deleted_at_utc
            VALUES (@user_id, @metadata);
            """;
        command.Parameters.Add("@user_id", SqlDbType.NVarChar, 100).Value = userId;
        command.Parameters.Add("@metadata", SqlDbType.NVarChar, -1).Value = (object?)metadata ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadWallet(reader);
    }

    public async Task<WalletAccount?> GetWalletAsync(
        Guid publicId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT public_id, user_id, metadata, created_at_utc, deleted_at_utc
            FROM dbo.wallets
            WHERE public_id = @public_id;
            """;
        command.Parameters.Add("@public_id", SqlDbType.UniqueIdentifier).Value = publicId;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadWallet(reader) : null;
    }

    public async Task<CreditReceipt> CreditAsync(
        Guid walletPublicId,
        CreditRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "dbo.usp_CreditWallet";
        command.Parameters.Add("@wallet_public_id", SqlDbType.UniqueIdentifier).Value = walletPublicId;
        command.Parameters.Add("@event_id", SqlDbType.UniqueIdentifier).Value = request.EventId;
        command.Parameters.Add("@amount", SqlDbType.Decimal).Value = request.Amount.Amount;
        command.Parameters.Add("@currency", SqlDbType.Char, 3).Value = request.Amount.Currency;
        command.Parameters.Add("@is_refundable", SqlDbType.Bit).Value = request.IsRefundable;
        command.Parameters.Add("@expiration_date", SqlDbType.DateTime2).Value =
            (object?)request.ExpirationUtc ?? DBNull.Value;

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return ReadReceipt(reader);
        }
        catch (SqlException ex) when (ex.Number == WalletNotFoundError)
        {
            throw new WalletNotFoundException(walletPublicId);
        }
    }

    public async Task<IReadOnlyList<CurrencyBalance>> GetBalancesAsync(
        Guid walletPublicId,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.currency, SUM(c.amount - ISNULL(a.spent, 0)) AS balance
            FROM dbo.wallet_credits c
            JOIN dbo.wallets w ON w.wallet_id = c.wallet_id
            OUTER APPLY
            (
                SELECT SUM(al.amount) AS spent
                FROM dbo.wallet_debit_allocations al
                WHERE al.credit_id = c.credit_id
            ) a
            WHERE w.public_id = @public_id
              AND w.deleted_at_utc IS NULL
              AND (c.expiration_date IS NULL OR c.expiration_date > @now_utc)
            GROUP BY c.currency
            ORDER BY c.currency;
            """;
        command.Parameters.Add("@public_id", SqlDbType.UniqueIdentifier).Value = walletPublicId;
        command.Parameters.Add("@now_utc", SqlDbType.DateTime2).Value = asOfUtc;

        var balances = new List<CurrencyBalance>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            balances.Add(new CurrencyBalance(reader.GetString(0).TrimEnd(), reader.GetDecimal(1)));
        }

        return balances;
    }

    private static WalletAccount ReadWallet(SqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.GetDateTime(3),
        reader.IsDBNull(4) ? null : reader.GetDateTime(4));

    private static CreditReceipt ReadReceipt(SqlDataReader reader)
    {
        // Columns: credit_id, event_id, amount, currency, is_refundable, expiration_date, created_at_utc, replayed
        var amount = Money.Create(reader.GetDecimal(2), reader.GetString(3).TrimEnd());
        return new CreditReceipt(
            reader.GetInt64(0),
            reader.GetGuid(1),
            amount,
            reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.GetDateTime(6),
            reader.GetBoolean(7));
    }
}
