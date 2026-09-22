# Database Schema

Status: agreed in milestone M1. This document is the design of record. The migration scripts in `db/migrations` implement it.

## Purpose

A wallet holds store credit as a set of small bags. Each bag (a `wallet_credit`) carries its own amount, currency and expiration. Spending money (a `wallet_debit`) consumes one or more bags in a defined order. The record of how a debit drew from each bag lives in `wallet_debit_allocations`.

## Design principles

- **Append-only money tables.** `wallet_credits`, `wallet_debits` and `wallet_debit_allocations` are only ever inserted, never updated or deleted. Every change is a new row, so the full history is always reconstructable.
- **Derived state.** A bag's remaining value and the wallet balance are computed from the rows, not stored in a mutable column. A covering index keeps these reads fast.
- **Multi-currency.** A wallet can hold bags in several currencies. Balance is reported per currency. A debit only draws from bags of its own currency.
- **Money as decimal.** All amounts are `DECIMAL(19,4)`, never `float`. This avoids binary rounding errors on money.
- **Idempotency.** Every credit and debit carries a client-supplied `event_id` with a unique constraint, so repeated or racing requests never apply twice.
- **ACID with explicit transactions.** Each money operation runs in one transaction that either commits fully or rolls back.

## Tables

### wallets

The aggregate root. It is the parent for the ledger tables and the row we lock to serialize debits. It is not a money table. The only update allowed is setting `deleted_at_utc`.

| Column | Type | Notes |
|---|---|---|
| wallet_id | BIGINT IDENTITY | PK, clustered |
| public_id | UNIQUEIDENTIFIER | id exposed in the API, DEFAULT NEWSEQUENTIALID(), unique index |
| user_id | NVARCHAR(100) NOT NULL | owner reference |
| metadata | NVARCHAR(MAX) NULL | free-form JSON attributes |
| created_at_utc | DATETIME2(3) NOT NULL | DEFAULT SYSUTCDATETIME() |
| deleted_at_utc | DATETIME2(3) NULL | NULL means active, a value means soft-deleted |

### wallet_credits

One row per bag. Inserted once, never changed. Its remaining value is derived (see below).

| Column | Type | Notes |
|---|---|---|
| credit_id | BIGINT IDENTITY | PK, clustered |
| wallet_id | BIGINT NOT NULL | FK to wallets |
| event_id | UNIQUEIDENTIFIER NOT NULL | idempotency key, unique |
| amount | DECIMAL(19,4) NOT NULL | original bag size, CHECK (amount > 0) |
| currency | CHAR(3) NOT NULL | ISO code |
| is_refundable | BIT NOT NULL | 1 means it can be withdrawn by the user, 0 means spend only |
| expiration_date | DATETIME2(3) NULL | NULL means never expires |
| created_at_utc | DATETIME2(3) NOT NULL | DEFAULT SYSUTCDATETIME() |

### wallet_debits

One row per spend request. Inserted once, never changed.

| Column | Type | Notes |
|---|---|---|
| debit_id | BIGINT IDENTITY | PK, clustered |
| wallet_id | BIGINT NOT NULL | FK to wallets |
| event_id | UNIQUEIDENTIFIER NOT NULL | idempotency key, unique |
| amount | DECIMAL(19,4) NOT NULL | total to spend, CHECK (amount > 0) |
| currency | CHAR(3) NOT NULL | must match the bags it draws from |
| kind | TINYINT NOT NULL | 1 = spend (any bag), 2 = withdrawal (refundable bags only), CHECK (kind IN (1,2)) |
| created_at_utc | DATETIME2(3) NOT NULL | DEFAULT SYSUTCDATETIME() |

### wallet_debit_allocations

One row per (debit, bag) pair. This is the record of how each credit was spent.

| Column | Type | Notes |
|---|---|---|
| allocation_id | BIGINT IDENTITY | PK, clustered |
| debit_id | BIGINT NOT NULL | FK to wallet_debits |
| credit_id | BIGINT NOT NULL | FK to wallet_credits |
| amount | DECIMAL(19,4) NOT NULL | portion taken from this bag, CHECK (amount > 0) |
| created_at_utc | DATETIME2(3) NOT NULL | DEFAULT SYSUTCDATETIME() |

Invariants (the sum of a debit's allocations equals the debit amount, and the sum of a bag's allocations never exceeds its amount) cannot be simple column checks. They are enforced by the debit procedure while it holds the wallet lock.

### __schema_versions

Bookkeeping for the ADO.NET migration runner. Not part of the money model.

| Column | Type | Notes |
|---|---|---|
| version | INT | PK, the numeric script prefix |
| script_name | NVARCHAR(260) NOT NULL | file name applied |
| applied_at_utc | DATETIME2(3) NOT NULL | DEFAULT SYSUTCDATETIME() |
| checksum | VARBINARY(32) NULL | hash of the script content |

## Derived values

Remaining value of one bag:

```sql
available(bag) = amount - ISNULL((SELECT SUM(a.amount)
                                  FROM wallet_debit_allocations a
                                  WHERE a.credit_id = bag.credit_id), 0)
```

Balance per currency for a wallet (only bags that have not expired):

```sql
SELECT c.currency,
       SUM(c.amount - ISNULL(a.spent, 0)) AS balance
FROM wallet_credits c
OUTER APPLY (
    SELECT SUM(al.amount) AS spent
    FROM wallet_debit_allocations al
    WHERE al.credit_id = c.credit_id
) a
WHERE c.wallet_id = @wallet_id
  AND (c.expiration_date IS NULL OR c.expiration_date > @now_utc)
GROUP BY c.currency;
```

## Expiration

In the MVP, expiration is handled purely by the read-time filter above (`expiration_date IS NULL OR expiration_date > @now_utc`). The moment a bag's `expiration_date` passes, it stops counting toward the balance. There is no write, no debit and no job, so the tables stay append-only and the balance is correct instantly. The read-time filter is the balance authority.

Note on tooling: a database trigger cannot expire a credit. Triggers react to data changes (INSERT, UPDATE, DELETE), not to the passage of time, and no row changes at the instant a credit expires. Materializing expiration (see deferred items) is the job of a schedule, not a trigger.

## Spend and allocation algorithm

Runs inside one transaction (see concurrency below).

1. Idempotency: if a `wallet_debits` row already exists for this `event_id`, return it and stop.
2. Select candidate bags for the wallet and the debit currency, not expired, ordered:
   `CASE WHEN expiration_date IS NULL THEN 1 ELSE 0 END, expiration_date, credit_id`.
   For a withdrawal (`kind = 2`), restrict to `is_refundable = 1`.
3. Walk the bags. Take from each the smaller of "remaining to cover" and "available in the bag". Insert one `wallet_debit_allocations` row per bag touched.
4. If the bags cannot cover the requested amount, THROW an insufficient-funds error, which rolls the whole transaction back.
5. Insert the `wallet_debits` row and commit.

## Concurrency and isolation

The money tables are append-only, so there is no single row that holds "current available" to lock. To stop two debits on the same wallet from over-spending a bag, each debit first takes an update lock on the wallet row:

```sql
SELECT 1 FROM wallets WITH (UPDLOCK, HOLDLOCK) WHERE wallet_id = @wallet_id;
```

The wallet row is only a gate here, it is not updated. Two debits on the same wallet queue, so the availability math is stable inside the transaction. Debits on different wallets still run in parallel. An alternative is `sp_getapplock` keyed on the wallet id. The unique index on `event_id` is the final backstop against duplicate application.

## Idempotency

`event_id` is a client-supplied `UNIQUEIDENTIFIER`, unique on both `wallet_credits` and `wallet_debits`. A repeated request with the same `event_id` is detected and the original result is returned. A racing duplicate that slips past the check hits the unique index and is treated as a replay.

## Indexes

- `PK_wallets`, `PK_wallet_credits`, `PK_wallet_debits`, `PK_wallet_debit_allocations`: clustered on the identity keys (narrow, ever-increasing).
- `UQ_wallets_public_id`: unique on `public_id`.
- `UQ_wallet_credits_event_id`: unique on `event_id` (idempotency).
- `UQ_wallet_debits_event_id`: unique on `event_id` (idempotency).
- `IX_wallet_credits_wallet_currency_expiration`: on `(wallet_id, currency, expiration_date)` INCLUDE `(amount, is_refundable)`. This is the covering index for both the balance read and the allocation candidate scan in expiration order. It replaces the old stored `available_amount` column with a fast, index-served read.
- `IX_wallet_debit_allocations_credit`: on `(credit_id)` INCLUDE `(amount)`. Makes "how much has this bag been spent" a covered seek.

The README will show the balance query plan before and after `IX_wallet_credits_wallet_currency_expiration`, with `SET STATISTICS IO ON` logical reads for each.

## Data type choices

- Money: `DECIMAL(19,4)`. Exact, no binary float error.
- Times: `DATETIME2(3)` in UTC. Millisecond precision is enough and it is smaller than the full `DATETIME2(7)`.
- Keys: `BIGINT IDENTITY` internal keys, `UNIQUEIDENTIFIER` public id. Small clustered keys keep the nonclustered indexes small.
- Enumerations: `BIT` for `is_refundable`, `TINYINT` with a CHECK for `kind`. Simple and cheap. A lookup table can replace them later if the set of values grows.

## Deferred (not in the MVP)

- Transfers between wallets (planned for M5, likely two debits and a credit linked by a correlation id).
- A statement/report reading index on `(wallet_id, created_at_utc)` for the history endpoint (M3).
- A balance snapshot/checkpoint table if the derived reads ever need to be faster. It would also be append-only.
- A richer wallet status beyond soft delete.
- **Materialized expiration events, so expirations appear on the statement and in the audit trail.** A scheduled job (a .NET `IHostedService` or a cron-triggered endpoint, not a trigger and not necessarily SQL Server Agent) runs on an interval, finds expired bags that still have a remaining amount, and inserts an expiration event modeled as a special debit (`kind = 3`) plus one allocation that consumes the bag's remaining amount. This stays append-only and flows through the same statement machinery as a normal debit. It does not double-count with the read-time filter: an expired bag contributes zero either way, and the filter covers the lag until the job runs, so the balance is never wrong in the meantime. When this is added, the `kind` CHECK expands to include 3.
- Whether the statement running balance should reflect expirations (only relevant once expiration events exist). Until then the statement shows credits and debits as pure movements.
