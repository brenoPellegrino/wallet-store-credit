-- Migration 005: the statement / history read.
--
-- usp_GetWalletStatement lists a wallet's movements in time order: each credit (money in) and each
-- debit (money out) as a row. It reports pure movements, it does not compute a running balance,
-- because a correct running balance would also have to account for expirations (see docs/schema.md,
-- deferred items). Callers present the sign from entry_type: credit is positive, debit is negative.

CREATE OR ALTER PROCEDURE dbo.usp_GetWalletStatement
    @wallet_public_id UNIQUEIDENTIFIER
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @wallet_id BIGINT =
    (
        SELECT wallet_id
        FROM dbo.wallets
        WHERE public_id = @wallet_public_id AND deleted_at_utc IS NULL
    );

    IF @wallet_id IS NULL
        THROW 50001, 'Wallet not found or has been deleted.', 1;

    SELECT entry_type, entry_id, event_id, amount, currency, kind, created_at_utc
    FROM
    (
        SELECT 'credit' AS entry_type, credit_id AS entry_id, event_id, amount, currency,
               CAST(NULL AS TINYINT) AS kind, created_at_utc
        FROM dbo.wallet_credits
        WHERE wallet_id = @wallet_id

        UNION ALL

        SELECT 'debit' AS entry_type, debit_id AS entry_id, event_id, amount, currency,
               kind, created_at_utc
        FROM dbo.wallet_debits
        WHERE wallet_id = @wallet_id
    ) movements
    ORDER BY created_at_utc, entry_type, entry_id;
END
