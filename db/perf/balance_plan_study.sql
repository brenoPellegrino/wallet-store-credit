-- Execution-plan study for the wallet balance read.
--
-- Builds a large, isolated WalletPerf database (so it never touches WalletDb), seeds 100,000 credit
-- bags across 200 wallets, then runs the real balance query twice: once with only the clustered
-- primary key, and once with the covering index IX_wallet_credits_wallet_currency_expiration.
-- STATISTICS IO reports the logical reads and SHOWPLAN_TEXT reports the plan operator, so the
-- before/after can be compared. See docs/execution-plans/balance-query.md for the captured numbers.
--
-- Run with: sqlcmd -S ... -U sa -P ... -C -i db/perf/balance_plan_study.sql

IF DB_ID('WalletPerf') IS NULL CREATE DATABASE WalletPerf;
GO
USE WalletPerf;
GO

DROP TABLE IF EXISTS dbo.wallet_debit_allocations;
DROP TABLE IF EXISTS dbo.wallet_credits;
DROP TABLE IF EXISTS dbo.wallets;
GO

CREATE TABLE dbo.wallets
(
    wallet_id      BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_perf_wallets PRIMARY KEY,
    public_id      UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_perf_wallets_public DEFAULT NEWID(),
    deleted_at_utc DATETIME2(3) NULL
);
CREATE UNIQUE INDEX UQ_perf_wallets_public ON dbo.wallets (public_id);

CREATE TABLE dbo.wallet_credits
(
    credit_id       BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_perf_credits PRIMARY KEY,
    wallet_id       BIGINT NOT NULL,
    amount          DECIMAL(19, 4) NOT NULL,
    currency        CHAR(3) NOT NULL,
    is_refundable   BIT NOT NULL,
    expiration_date DATETIME2(3) NULL
);

CREATE TABLE dbo.wallet_debit_allocations
(
    allocation_id BIGINT IDENTITY(1, 1) NOT NULL CONSTRAINT PK_perf_allocations PRIMARY KEY,
    credit_id     BIGINT NOT NULL,
    amount        DECIMAL(19, 4) NOT NULL
);
CREATE INDEX IX_perf_allocations_credit ON dbo.wallet_debit_allocations (credit_id) INCLUDE (amount);
GO

-- 200 wallets, 100,000 USD bags spread across them (500 bags per wallet).
INSERT dbo.wallets (public_id) SELECT TOP (200) NEWID() FROM sys.all_objects;

DECLARE @firstWallet BIGINT = (SELECT MIN(wallet_id) FROM dbo.wallets);

;WITH n AS
(
    SELECT TOP (100000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT dbo.wallet_credits (wallet_id, amount, currency, is_refundable, expiration_date)
SELECT @firstWallet + (rn % 200), 1.0000, 'USD', 0, NULL
FROM n;
GO

DECLARE @pid UNIQUEIDENTIFIER = (SELECT public_id FROM dbo.wallets WHERE wallet_id = (SELECT MIN(wallet_id) FROM dbo.wallets));
SELECT @pid AS target_public_id, COUNT(*) AS total_credit_rows FROM dbo.wallet_credits;
GO

PRINT '';
PRINT '==================== BEFORE: no covering index (clustered PK only) ====================';
GO
SET STATISTICS IO ON;
DECLARE @pid UNIQUEIDENTIFIER = (SELECT public_id FROM dbo.wallets WHERE wallet_id = (SELECT MIN(wallet_id) FROM dbo.wallets));
DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
SELECT c.currency, SUM(c.amount - ISNULL(a.spent, 0)) AS balance
FROM dbo.wallet_credits c
JOIN dbo.wallets w ON w.wallet_id = c.wallet_id
OUTER APPLY (SELECT SUM(al.amount) AS spent FROM dbo.wallet_debit_allocations al WHERE al.credit_id = c.credit_id) a
WHERE w.public_id = @pid AND w.deleted_at_utc IS NULL AND (c.expiration_date IS NULL OR c.expiration_date > @now)
GROUP BY c.currency;
SET STATISTICS IO OFF;
GO

PRINT '';
PRINT '-------------------- BEFORE: plan --------------------';
GO
SET SHOWPLAN_TEXT ON;
GO
DECLARE @pid UNIQUEIDENTIFIER = (SELECT public_id FROM dbo.wallets WHERE wallet_id = (SELECT MIN(wallet_id) FROM dbo.wallets));
DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
SELECT c.currency, SUM(c.amount - ISNULL(a.spent, 0)) AS balance
FROM dbo.wallet_credits c
JOIN dbo.wallets w ON w.wallet_id = c.wallet_id
OUTER APPLY (SELECT SUM(al.amount) AS spent FROM dbo.wallet_debit_allocations al WHERE al.credit_id = c.credit_id) a
WHERE w.public_id = @pid AND w.deleted_at_utc IS NULL AND (c.expiration_date IS NULL OR c.expiration_date > @now)
GROUP BY c.currency;
GO
SET SHOWPLAN_TEXT OFF;
GO

CREATE INDEX IX_wallet_credits_wallet_currency_expiration
    ON dbo.wallet_credits (wallet_id, currency, expiration_date)
    INCLUDE (amount, is_refundable);
GO

PRINT '';
PRINT '==================== AFTER: with covering index ====================';
GO
SET STATISTICS IO ON;
DECLARE @pid UNIQUEIDENTIFIER = (SELECT public_id FROM dbo.wallets WHERE wallet_id = (SELECT MIN(wallet_id) FROM dbo.wallets));
DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
SELECT c.currency, SUM(c.amount - ISNULL(a.spent, 0)) AS balance
FROM dbo.wallet_credits c
JOIN dbo.wallets w ON w.wallet_id = c.wallet_id
OUTER APPLY (SELECT SUM(al.amount) AS spent FROM dbo.wallet_debit_allocations al WHERE al.credit_id = c.credit_id) a
WHERE w.public_id = @pid AND w.deleted_at_utc IS NULL AND (c.expiration_date IS NULL OR c.expiration_date > @now)
GROUP BY c.currency;
SET STATISTICS IO OFF;
GO

PRINT '';
PRINT '-------------------- AFTER: plan --------------------';
GO
SET SHOWPLAN_TEXT ON;
GO
DECLARE @pid UNIQUEIDENTIFIER = (SELECT public_id FROM dbo.wallets WHERE wallet_id = (SELECT MIN(wallet_id) FROM dbo.wallets));
DECLARE @now DATETIME2(3) = SYSUTCDATETIME();
SELECT c.currency, SUM(c.amount - ISNULL(a.spent, 0)) AS balance
FROM dbo.wallet_credits c
JOIN dbo.wallets w ON w.wallet_id = c.wallet_id
OUTER APPLY (SELECT SUM(al.amount) AS spent FROM dbo.wallet_debit_allocations al WHERE al.credit_id = c.credit_id) a
WHERE w.public_id = @pid AND w.deleted_at_utc IS NULL AND (c.expiration_date IS NULL OR c.expiration_date > @now)
GROUP BY c.currency;
GO
SET SHOWPLAN_TEXT OFF;
GO
