-- Migration 001: core wallet tables.
--
-- Creates the append-only money model agreed in M1 (see docs/schema.md): wallets and the
-- three ledger tables, with primary keys, foreign keys and the unique indexes that back
-- idempotency. The covering index for balance reads is added in a later migration (M3/M4),
-- so the balance query here runs without it on purpose: that is the "before" state for the
-- execution-plan study.

CREATE TABLE dbo.wallets
(
    wallet_id       BIGINT           IDENTITY(1, 1) NOT NULL,
    public_id       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_wallets_public_id DEFAULT NEWSEQUENTIALID(),
    user_id         NVARCHAR(100)    NOT NULL,
    metadata        NVARCHAR(MAX)    NULL,
    created_at_utc  DATETIME2(3)     NOT NULL CONSTRAINT DF_wallets_created_at DEFAULT SYSUTCDATETIME(),
    deleted_at_utc  DATETIME2(3)     NULL,
    CONSTRAINT PK_wallets PRIMARY KEY CLUSTERED (wallet_id)
);

CREATE UNIQUE INDEX UQ_wallets_public_id ON dbo.wallets (public_id);

CREATE TABLE dbo.wallet_credits
(
    credit_id        BIGINT           IDENTITY(1, 1) NOT NULL,
    wallet_id        BIGINT           NOT NULL,
    event_id         UNIQUEIDENTIFIER NOT NULL,
    amount           DECIMAL(19, 4)   NOT NULL CONSTRAINT CK_wallet_credits_amount CHECK (amount > 0),
    currency         CHAR(3)          NOT NULL,
    is_refundable    BIT              NOT NULL,
    expiration_date  DATETIME2(3)     NULL,
    created_at_utc   DATETIME2(3)     NOT NULL CONSTRAINT DF_wallet_credits_created_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_wallet_credits PRIMARY KEY CLUSTERED (credit_id),
    CONSTRAINT FK_wallet_credits_wallet FOREIGN KEY (wallet_id) REFERENCES dbo.wallets (wallet_id)
);

CREATE UNIQUE INDEX UQ_wallet_credits_event_id ON dbo.wallet_credits (event_id);

CREATE TABLE dbo.wallet_debits
(
    debit_id        BIGINT           IDENTITY(1, 1) NOT NULL,
    wallet_id       BIGINT           NOT NULL,
    event_id        UNIQUEIDENTIFIER NOT NULL,
    amount          DECIMAL(19, 4)   NOT NULL CONSTRAINT CK_wallet_debits_amount CHECK (amount > 0),
    currency        CHAR(3)          NOT NULL,
    kind            TINYINT          NOT NULL CONSTRAINT CK_wallet_debits_kind CHECK (kind IN (1, 2)),
    created_at_utc  DATETIME2(3)     NOT NULL CONSTRAINT DF_wallet_debits_created_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_wallet_debits PRIMARY KEY CLUSTERED (debit_id),
    CONSTRAINT FK_wallet_debits_wallet FOREIGN KEY (wallet_id) REFERENCES dbo.wallets (wallet_id)
);

CREATE UNIQUE INDEX UQ_wallet_debits_event_id ON dbo.wallet_debits (event_id);

CREATE TABLE dbo.wallet_debit_allocations
(
    allocation_id   BIGINT         IDENTITY(1, 1) NOT NULL,
    debit_id        BIGINT         NOT NULL,
    credit_id       BIGINT         NOT NULL,
    amount          DECIMAL(19, 4) NOT NULL CONSTRAINT CK_wallet_debit_allocations_amount CHECK (amount > 0),
    created_at_utc  DATETIME2(3)   NOT NULL CONSTRAINT DF_wallet_debit_allocations_created_at DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_wallet_debit_allocations PRIMARY KEY CLUSTERED (allocation_id),
    CONSTRAINT FK_wallet_debit_allocations_debit FOREIGN KEY (debit_id) REFERENCES dbo.wallet_debits (debit_id),
    CONSTRAINT FK_wallet_debit_allocations_credit FOREIGN KEY (credit_id) REFERENCES dbo.wallet_credits (credit_id)
);

CREATE INDEX IX_wallet_debit_allocations_credit ON dbo.wallet_debit_allocations (credit_id) INCLUDE (amount);
