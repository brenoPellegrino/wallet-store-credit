-- Migration 004: the debit money path.
--
-- usp_DebitWallet spends @amount from a wallet's credit bags, oldest-expiring first, and records
-- exactly how much it took from each bag in wallet_debit_allocations. It is idempotent on
-- @event_id and runs inside one transaction that either commits fully or rolls back, so a balance
-- is never left half spent.
--
-- Concurrency: two debits on the same wallet must not both read the same "available" and overspend
-- a bag. Each debit first takes an update lock on the wallet row, so debits on one wallet serialize
-- while debits on different wallets still run in parallel. The unique index on event_id is the final
-- backstop against a duplicate.
--
-- @kind: 1 = spend (any bag), 2 = withdrawal (refundable bags only).
--
-- The proc returns two result sets: the debit row, then its allocations (credit_id, amount).

CREATE OR ALTER PROCEDURE dbo.usp_DebitWallet
    @wallet_public_id UNIQUEIDENTIFIER,
    @event_id         UNIQUEIDENTIFIER,
    @amount           DECIMAL(19, 4),
    @currency         CHAR(3),
    @kind             TINYINT,
    @now_utc          DATETIME2(3)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- Serialize debits on this wallet by taking an update lock on its row (the row is a gate,
        -- it is not modified). Debits on other wallets are unaffected.
        DECLARE @wallet_id BIGINT;
        SELECT @wallet_id = wallet_id
        FROM dbo.wallets WITH (UPDLOCK, HOLDLOCK)
        WHERE public_id = @wallet_public_id AND deleted_at_utc IS NULL;

        IF @wallet_id IS NULL
            THROW 50001, 'Wallet not found or has been deleted.', 1;

        -- Idempotency: this event was already applied, return the original debit unchanged.
        DECLARE @debit_id BIGINT = (SELECT debit_id FROM dbo.wallet_debits WHERE event_id = @event_id);
        IF @debit_id IS NOT NULL
        BEGIN
            SELECT debit_id, event_id, amount, currency, kind, created_at_utc, CAST(1 AS BIT) AS replayed
            FROM dbo.wallet_debits WHERE debit_id = @debit_id;

            SELECT credit_id, amount
            FROM dbo.wallet_debit_allocations WHERE debit_id = @debit_id ORDER BY allocation_id;

            COMMIT TRANSACTION;
            RETURN;
        END

        -- Work out how much to take from each candidate bag. before_sum is the available amount in
        -- the bags ahead of this one in spend order; a bag is used only while the bags before it have
        -- not already covered the debit, and it gives the smaller of its available amount and what is
        -- still left to cover.
        DECLARE @allocations TABLE (ord INT PRIMARY KEY, credit_id BIGINT NOT NULL, amount DECIMAL(19, 4) NOT NULL);

        ;WITH candidates AS
        (
            SELECT c.credit_id,
                   c.amount - ISNULL(a.spent, 0) AS available,
                   ROW_NUMBER() OVER (
                       ORDER BY CASE WHEN c.expiration_date IS NULL THEN 1 ELSE 0 END,
                                c.expiration_date,
                                c.credit_id) AS rn
            FROM dbo.wallet_credits c
            OUTER APPLY
            (
                SELECT SUM(al.amount) AS spent
                FROM dbo.wallet_debit_allocations al
                WHERE al.credit_id = c.credit_id
            ) a
            WHERE c.wallet_id = @wallet_id
              AND c.currency = @currency
              AND (c.expiration_date IS NULL OR c.expiration_date > @now_utc)
              AND (@kind <> 2 OR c.is_refundable = 1)
        ),
        available_bags AS
        (
            SELECT credit_id, available, rn
            FROM candidates
            WHERE available > 0
        ),
        running AS
        (
            SELECT credit_id,
                   available,
                   rn,
                   ISNULL(SUM(available) OVER (ORDER BY rn ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS before_sum
            FROM available_bags
        )
        INSERT INTO @allocations (ord, credit_id, amount)
        SELECT rn,
               credit_id,
               CASE WHEN @amount - before_sum >= available THEN available ELSE @amount - before_sum END
        FROM running
        WHERE before_sum < @amount;

        -- Did the bags cover the requested amount? If not, roll the whole debit back.
        IF (SELECT ISNULL(SUM(amount), 0) FROM @allocations) < @amount
            THROW 50002, 'Insufficient funds for this debit.', 1;

        INSERT dbo.wallet_debits (wallet_id, event_id, amount, currency, kind)
        VALUES (@wallet_id, @event_id, @amount, @currency, @kind);

        SET @debit_id = SCOPE_IDENTITY();

        -- ORDER BY ord so allocation_id follows spend order and the allocations read back oldest-first.
        INSERT dbo.wallet_debit_allocations (debit_id, credit_id, amount)
        SELECT @debit_id, credit_id, amount
        FROM @allocations
        ORDER BY ord;

        SELECT debit_id, event_id, amount, currency, kind, created_at_utc, CAST(0 AS BIT) AS replayed
        FROM dbo.wallet_debits WHERE debit_id = @debit_id;

        SELECT credit_id, amount
        FROM dbo.wallet_debit_allocations WHERE debit_id = @debit_id ORDER BY allocation_id;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        -- A racing duplicate slipped past the idempotency check and hit the unique index: report the
        -- debit that won as a replay rather than surfacing a key-violation error.
        IF ERROR_NUMBER() IN (2601, 2627)
        BEGIN
            DECLARE @winner BIGINT = (SELECT debit_id FROM dbo.wallet_debits WHERE event_id = @event_id);

            SELECT debit_id, event_id, amount, currency, kind, created_at_utc, CAST(1 AS BIT) AS replayed
            FROM dbo.wallet_debits WHERE debit_id = @winner;

            SELECT credit_id, amount
            FROM dbo.wallet_debit_allocations WHERE debit_id = @winner ORDER BY allocation_id;

            RETURN;
        END;

        THROW;
    END CATCH
END
