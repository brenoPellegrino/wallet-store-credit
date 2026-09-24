# Execution-plan study: the balance read

This is the before/after study promised in the plan for the covering index
`IX_wallet_credits_wallet_currency_expiration`. It measures the wallet balance query, the most
interesting read in the project, against a large data set with and without the index.

## The query

The balance per currency for one wallet, counting only bags that have not expired (see
`docs/schema.md` and `WalletRepository.GetBalancesAsync`):

```sql
SELECT c.currency, SUM(c.amount - ISNULL(a.spent, 0)) AS balance
FROM dbo.wallet_credits c
JOIN dbo.wallets w ON w.wallet_id = c.wallet_id
OUTER APPLY (SELECT SUM(al.amount) AS spent
             FROM dbo.wallet_debit_allocations al
             WHERE al.credit_id = c.credit_id) a
WHERE w.public_id = @pid
  AND w.deleted_at_utc IS NULL
  AND (c.expiration_date IS NULL OR c.expiration_date > @now)
GROUP BY c.currency;
```

## Setup

`db/perf/balance_plan_study.sql` builds an isolated `WalletPerf` database and seeds **100,000
credit bags across 200 wallets** (500 bags per wallet). The target wallet has 500 USD bags. The
script runs the query twice, once with only the clustered primary key and once after creating the
covering index, capturing `SET STATISTICS IO` and `SET SHOWPLAN_TEXT`.

Reproduce:

```bash
docker compose up -d
docker exec -i wallet-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -W \
  < db/perf/balance_plan_study.sql
```

## Result

Logical reads on `wallet_credits`, reading one wallet's 500 bags out of 100,000 rows:

| | `wallet_credits` operator | `wallet_credits` logical reads |
|---|---|---|
| **Before** (clustered PK only) | Clustered Index **Scan** | **563** |
| **After** (covering index) | Index **Seek** | **6** |

That is a **~94x** drop in logical reads for this wallet, and it does not grow with the table:
before the index the query reads every page of `wallet_credits`, so the cost climbs with total rows
across all wallets; after the index it seeks straight to the target wallet's key range and reads
only those pages.

### Before: Clustered Index Scan

```
|--Clustered Index Scan(OBJECT:(...wallet_credits.PK_perf_credits AS [c]),
     WHERE:([c].[expiration_date] IS NULL OR [c].[expiration_date] > [@now]))
```

The optimizer has no way to find one wallet's bags except to scan the whole clustered index and
filter, so it touches all 100,000 rows.

### After: Index Seek on the covering index

```
|--Index Seek(OBJECT:(...wallet_credits.IX_wallet_credits_wallet_currency_expiration AS [c]),
     SEEK:([c].[wallet_id] = [w].[wallet_id]),
     WHERE:([c].[expiration_date] IS NULL OR [c].[expiration_date] > [@now]))
```

The index leads on `wallet_id`, so the query seeks to exactly this wallet's rows. It is a **covering**
index: `currency` and `expiration_date` are in the key and `amount` and `is_refundable` are `INCLUDE`
columns, so every column the query needs is in the index and there is no key lookup back to the base
table.

## Notes

- `wallets` is a couple of logical reads either way (a seek on `UQ_wallets_public_id`), so it is not
  the interesting part.
- `wallet_debit_allocations` is empty in this study, which isolates the covering-index effect on
  `wallet_credits`. Its own seek index (`(credit_id) INCLUDE (amount)`) keeps the `OUTER APPLY` cheap
  once allocations exist.
- The same covering index also serves the debit allocation scan (`usp_DebitWallet`), which selects a
  wallet's unexpired bags of one currency in expiration order.
