-- Migration 002: the credit money path.
--
-- usp_CreditWallet adds one credit bag to a wallet. It is idempotent on @event_id: replaying a
-- request that was already applied returns the existing bag with replayed = 1 and inserts nothing.
-- A request that races past the existence check is caught by the unique index on event_id and is
-- also reported as a replay, so concurrent duplicates never create two bags.
--
-- Credits do not need the wallet-row lock that debits use: each credit is an independent insert
-- and the unique index is the only serialization point they need.

CREATE OR ALTER PROCEDURE dbo.usp_CreditWallet
    @wallet_public_id UNIQUEIDENTIFIER,
    @event_id         UNIQUEIDENTIFIER,
    @amount           DECIMAL(19, 4),
    @currency         CHAR(3),
    @is_refundable    BIT,
    @expiration_date  DATETIME2(3) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @wallet_id BIGINT =
    (
        SELECT wallet_id
        FROM dbo.wallets
        WHERE public_id = @wallet_public_id
          AND deleted_at_utc IS NULL
    );

    IF @wallet_id IS NULL
        THROW 50001, 'Wallet not found or has been deleted.', 1;

    -- Fast idempotency path: this event was already applied.
    IF EXISTS (SELECT 1 FROM dbo.wallet_credits WHERE event_id = @event_id)
    BEGIN
        SELECT credit_id, event_id, amount, currency, is_refundable,
               expiration_date, created_at_utc, CAST(1 AS BIT) AS replayed
        FROM dbo.wallet_credits
        WHERE event_id = @event_id;
        RETURN;
    END

    BEGIN TRY
        INSERT dbo.wallet_credits (wallet_id, event_id, amount, currency, is_refundable, expiration_date)
        VALUES (@wallet_id, @event_id, @amount, @currency, @is_refundable, @expiration_date);

        SELECT credit_id, event_id, amount, currency, is_refundable,
               expiration_date, created_at_utc, CAST(0 AS BIT) AS replayed
        FROM dbo.wallet_credits
        WHERE credit_id = SCOPE_IDENTITY();
    END TRY
    BEGIN CATCH
        -- 2601/2627: a racing request inserted the same event_id first. Treat it as a replay.
        IF ERROR_NUMBER() IN (2601, 2627)
            SELECT credit_id, event_id, amount, currency, is_refundable,
                   expiration_date, created_at_utc, CAST(1 AS BIT) AS replayed
            FROM dbo.wallet_credits
            WHERE event_id = @event_id;
        ELSE
            THROW;
    END CATCH
END
