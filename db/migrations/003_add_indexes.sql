-- Migration 003: read indexes for balance, allocation and statement.
--
-- These were deferred until there were queries to serve (see docs/schema.md). The covering index
-- is the one the M4 execution-plan study profiles: it serves both the balance read and the debit
-- allocation scan (same wallet, same currency, unexpired bags, in expiration order) straight from
-- the index, without touching the base table.

CREATE INDEX IX_wallet_credits_wallet_currency_expiration
    ON dbo.wallet_credits (wallet_id, currency, expiration_date)
    INCLUDE (amount, is_refundable);

-- Statement reads list a wallet's movements newest-or-oldest first, so both ledgers get an index
-- on (wallet_id, created_at_utc).
CREATE INDEX IX_wallet_credits_wallet_created
    ON dbo.wallet_credits (wallet_id, created_at_utc);

CREATE INDEX IX_wallet_debits_wallet_created
    ON dbo.wallet_debits (wallet_id, created_at_utc);
