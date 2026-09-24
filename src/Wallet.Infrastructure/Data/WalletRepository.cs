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
    // Custom error numbers the procedures THROW.
    private const int WalletNotFoundError = 50001;
    private const int InsufficientFundsError = 50002;

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
        return await ExecuteCreditAsync(connection, transaction: null, walletPublicId, request, cancellationToken);
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

    public async Task<DebitReceipt> DebitAsync(
        Guid walletPublicId,
        DebitRequest request,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenAsync(cancellationToken);
        return await ExecuteDebitAsync(connection, transaction: null, walletPublicId, request, asOfUtc, cancellationToken);
    }

    public async Task<TransferReceipt> TransferAsync(
        Guid sourcePublicId,
        Guid destinationPublicId,
        TransferRequest request,
        DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            // Both operations share one transaction: the debit from the source and the credit to the
            // destination either commit together or not at all. The same event_id makes the whole
            // transfer idempotent (a replay finds both rows already present and adds nothing).
            var debit = await ExecuteDebitAsync(
                connection, transaction, sourcePublicId,
                new DebitRequest(request.EventId, request.Amount, DebitKind.Spend), asOfUtc, cancellationToken);

            var credit = await ExecuteCreditAsync(
                connection, transaction, destinationPublicId,
                new CreditRequest(request.EventId, request.Amount, IsRefundable: true), cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new TransferReceipt(
                request.EventId, request.Amount, sourcePublicId, destinationPublicId,
                debit, credit, debit.Replayed && credit.Replayed);
        }
        catch
        {
            // A failing proc may already have rolled the transaction back (XACT_ABORT), so this is
            // best effort. Either way nothing from the transfer is committed.
            await TryRollbackAsync(transaction, cancellationToken);
            throw;
        }
    }

    private static async Task<CreditReceipt> ExecuteCreditAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid walletPublicId,
        CreditRequest request,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
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

    private static async Task<DebitReceipt> ExecuteDebitAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid walletPublicId,
        DebitRequest request,
        DateTime asOfUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "dbo.usp_DebitWallet";
        command.Parameters.Add("@wallet_public_id", SqlDbType.UniqueIdentifier).Value = walletPublicId;
        command.Parameters.Add("@event_id", SqlDbType.UniqueIdentifier).Value = request.EventId;
        command.Parameters.Add("@amount", SqlDbType.Decimal).Value = request.Amount.Amount;
        command.Parameters.Add("@currency", SqlDbType.Char, 3).Value = request.Amount.Currency;
        command.Parameters.Add("@kind", SqlDbType.TinyInt).Value = (byte)request.Kind;
        command.Parameters.Add("@now_utc", SqlDbType.DateTime2).Value = asOfUtc;

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // First result set: the debit row.
            await reader.ReadAsync(cancellationToken);
            var debitId = reader.GetInt64(0);
            var eventId = reader.GetGuid(1);
            var amount = Money.Create(reader.GetDecimal(2), reader.GetString(3).TrimEnd());
            var kind = (DebitKind)reader.GetByte(4);
            var createdAt = reader.GetDateTime(5);
            var replayed = reader.GetBoolean(6);

            // Second result set: how the debit drew from each bag.
            var allocations = new List<DebitAllocation>();
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                allocations.Add(new DebitAllocation(reader.GetInt64(0), reader.GetDecimal(1)));
            }

            return new DebitReceipt(debitId, eventId, amount, kind, createdAt, replayed, allocations);
        }
        catch (SqlException ex) when (ex.Number == WalletNotFoundError)
        {
            throw new WalletNotFoundException(walletPublicId);
        }
        catch (SqlException ex) when (ex.Number == InsufficientFundsError)
        {
            throw new InsufficientFundsException(walletPublicId, request.Amount);
        }
    }

    private static async Task TryRollbackAsync(SqlTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await transaction.RollbackAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
            // The transaction was already rolled back by the server (XACT_ABORT). Nothing to undo.
        }
    }

    public async Task<IReadOnlyList<StatementEntry>> GetStatementAsync(
        Guid walletPublicId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.CreateOpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandType = CommandType.StoredProcedure;
        command.CommandText = "dbo.usp_GetWalletStatement";
        command.Parameters.Add("@wallet_public_id", SqlDbType.UniqueIdentifier).Value = walletPublicId;

        try
        {
            var entries = new List<StatementEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                // Columns: entry_type, entry_id, event_id, amount, currency, kind, created_at_utc
                var type = reader.GetString(0) == "credit" ? MovementType.Credit : MovementType.Debit;
                var amount = Money.Create(reader.GetDecimal(3), reader.GetString(4).TrimEnd());
                DebitKind? kind = reader.IsDBNull(5) ? null : (DebitKind)reader.GetByte(5);
                entries.Add(new StatementEntry(type, reader.GetInt64(1), reader.GetGuid(2), amount, kind, reader.GetDateTime(6)));
            }

            return entries;
        }
        catch (SqlException ex) when (ex.Number == WalletNotFoundError)
        {
            throw new WalletNotFoundException(walletPublicId);
        }
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
