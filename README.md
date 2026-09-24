# Wallet / Store-Credit System

A standalone wallet and store-credit service. It is a C# / .NET 8 Web API backed by SQL Server,
written to show depth in **SQL, raw ADO.NET and stored procedures** on the paths that move money.

The interesting part of this project is not the API surface, it is what happens underneath: an
append-only ledger, idempotent money operations, a concurrency-safe debit, a client-side
transaction for transfers and a covering index whose value is measured, not assumed.

## Contents

- [What it does](#what-it-does)
- [Quick start](#quick-start)
- [Architecture](#architecture)
- [The data model](#the-data-model)
- [The money paths and their guarantees](#the-money-paths-and-their-guarantees)
- [Stored procedures](#stored-procedures)
- [Schema versioning](#schema-versioning)
- [Performance: the covering index, measured](#performance-the-covering-index-measured)
- [API](#api)
- [Testing and CI](#testing-and-ci)
- [Key decisions](#key-decisions)
- [Running locally](#running-locally)
- [Repository layout](#repository-layout)

## What it does

A wallet holds store credit as a set of small bags. Each **credit** bag carries its own amount,
currency and optional expiration. Spending money (a **debit**) consumes one or more bags in a
defined order, and the record of how a debit drew from each bag lives in an allocations table. A
**transfer** moves money between two wallets. Balances are derived from the ledger per currency,
never stored in a mutable column.

Everything that changes money is **append-only**: rows are inserted, never updated or deleted, so
the full history is always reconstructable.

## Quick start

The whole system runs from Docker, with no need to install .NET or SQL Server locally:

```bash
cp .env.example .env          # set a strong MSSQL_SA_PASSWORD
docker compose --profile app up --build
```

Then open **http://localhost:8080/swagger** and walk the flow: create a wallet, credit it, debit
it, transfer to another wallet, and read the balance and statement.

SQL Server has no native Apple Silicon build, so on arm64 it runs the x64 image under emulation.
That is fine for development. The API image is native to the host.

## Architecture

Three projects, with a clean dependency direction (`Api -> Infrastructure -> Core`):

| Project | Responsibility |
|---|---|
| `Wallet.Core` | Domain: the `Money` value type, models, the `IWalletRepository` contract, errors. No infrastructure. |
| `Wallet.Infrastructure` | Raw ADO.NET data access, the stored-procedure calls, and the migration runner. |
| `Wallet.Api` | ASP.NET Core Web API (controllers), request/response contracts, error mapping. |

**There is no ORM.** Every path is raw ADO.NET (`SqlConnection`, `SqlCommand`, `SqlDataReader`,
`SqlTransaction`). Money mutations go through stored procedures; reads are hand-written SQL. This is
a deliberate choice so the one interesting read, the balance aggregate, stays hand-written and
deterministic for the performance study (see [ADR 0001](docs/adr/0001-no-orm-raw-ado-net.md)).

## The data model

Four tables, all append-only except for a soft-delete flag on `wallets`. The full design and the
reasoning behind every column, key and constraint is in [`docs/schema.md`](docs/schema.md).

```
wallets ─┬─< wallet_credits ─────< wallet_debit_allocations >───── wallet_debits >─┬─ wallets
         │        (bags)                 (how a debit drew                (spends)  │
         └──────────────────────────── from each bag) ───────────────────────────┘
```

- **`wallets`** is the aggregate root and the row locked to serialize debits.
- **`wallet_credits`** is one row per bag: amount, currency, refundable flag, optional expiration.
- **`wallet_debits`** is one row per spend request.
- **`wallet_debit_allocations`** records, per (debit, bag) pair, how much a debit took from a bag.

Two derived values are computed from these rows, never stored:

- A bag's **remaining** value is its amount minus the sum of its allocations.
- A wallet's **balance per currency** is the sum of remaining value over its unexpired bags.

Money is `DECIMAL(19,4)` in the database and `decimal` in C#, never `float`. Expiration is handled
by a read-time filter (`expiration_date IS NULL OR expiration_date > @now`), so a bag stops counting
the instant it expires, with no job and no write.

## The money paths and their guarantees

- **Idempotency.** Every credit, debit and transfer carries a client-supplied `event_id` with a
  unique constraint. A repeated or racing request is detected and the original result is returned, so
  a retry never applies twice. A transfer uses the same `event_id` for both its debit and its credit,
  which also correlates the two rows.
- **Concurrency safety.** Two debits on the same wallet must not both read the same available balance
  and overspend a bag. Each debit first takes an update lock (`UPDLOCK, HOLDLOCK`) on the wallet row,
  so debits on one wallet serialize while debits on different wallets still run in parallel. This is
  proven by a test that fires many debits at the same instant and asserts the money adds up.
- **ACID transactions.** Each single money operation runs in one transaction that commits fully or
  rolls back. A **transfer** goes further: it opens a client-side `SqlTransaction` in C# and runs the
  source debit and the destination credit inside it, so they commit together or not at all. If the
  destination does not exist, the already-executed debit is rolled back and the source keeps its full
  balance.

## Stored procedures

The money mutations are stored procedures, called over ADO.NET:

- **`usp_CreditWallet`** adds a credit bag, idempotent on `event_id` (a racing duplicate is caught by
  the unique index and reported as a replay).
- **`usp_DebitWallet`** spends from a wallet. It takes the wallet-row lock, selects candidate bags
  (right currency, not expired, refundable-only for a withdrawal) in oldest-expiring-first order, and
  works out how much to take from each with a set-based running total. If the bags cannot cover the
  amount it throws and the whole debit rolls back. It returns the debit row and its allocations.
- **`usp_GetWalletStatement`** lists a wallet's credits and debits as time-ordered movements.

The definitions live in [`db/migrations`](db/migrations) as numbered scripts.

## Schema versioning

A hand-rolled ADO.NET **migration runner** applies numbered `.sql` scripts (tables, procedures and
indexes) in order, each inside its own `SqlTransaction`. It records every applied script in
`__schema_versions` with a SHA-256 checksum, and re-verifies that checksum on later runs so an
edited migration fails loudly. It also creates the target database on first run, so a fresh SQL
Server needs no manual setup. The API applies pending migrations on startup.

## Performance: the covering index, measured

The balance read is an expiration-filtered per-currency aggregate. A covering index,
`IX_wallet_credits_wallet_currency_expiration` on `(wallet_id, currency, expiration_date)` with
`INCLUDE (amount, is_refundable)`, serves it (and the debit allocation scan).

The value is measured, not assumed. Against 100,000 credit bags across 200 wallets, for one wallet's
balance:

| | `wallet_credits` operator | logical reads |
|---|---|---|
| Before | Clustered Index **Scan** | **563** |
| After | Index **Seek** | **6** |

The scan's cost grows with the total number of rows; the seek's cost tracks only the wallet's own
bags. Full study and how to reproduce it: [`docs/execution-plans/balance-query.md`](docs/execution-plans/balance-query.md)
and [`db/perf/balance_plan_study.sql`](db/perf/balance_plan_study.sql).

## API

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/wallets` | Create a wallet |
| `GET` | `/wallets/{id}` | Get a wallet |
| `POST` | `/wallets/{id}/credits` | Credit (money in), idempotent on `eventId` |
| `POST` | `/wallets/{id}/debits` | Debit (money out), 409 on insufficient funds |
| `GET` | `/wallets/{id}/balances` | Balance per currency |
| `GET` | `/wallets/{id}/statement` | Movement history |
| `POST` | `/transfers` | Move money between two wallets, in one transaction |
| `GET` | `/health`, `/health/db` | Liveness and database readiness |

Errors are returned as `ProblemDetails`: 400 for invalid input, 404 for a missing wallet, 409 when a
debit or transfer cannot be covered.

## Testing and CI

- **Unit tests** (`Wallet.UnitTests`) cover the `Money` type, no database needed.
- **Integration tests** (`Wallet.IntegrationTests`) run against real SQL Server through an in-memory
  host, and cover idempotent replay, overdraft rollback, oldest-first allocation, concurrency,
  transfer commit and rollback, and the HTTP surface. They skip themselves cleanly when SQL Server is
  not reachable, so the suite is green without a database.
- **CI** (`.github/workflows/ci.yml`) builds and runs the full suite on every pull request, with SQL
  Server 2022 as a service container. The `build-and-test` check is required on `main` and `dev`, so
  nothing merges red. There is no CD: the project is not deployed anywhere.

## Key decisions

Larger decisions are recorded as ADRs:

- [ADR 0001: raw ADO.NET everywhere, no ORM](docs/adr/0001-no-orm-raw-ado-net.md)
- [ADR 0002: SQL Server in Docker](docs/adr/0002-sql-server-in-docker.md)

The build plan and milestone history is in [`PLAN.md`](PLAN.md).

## Running locally

Prerequisites: Docker, and .NET 8 SDK if you want to run the API outside a container.

```bash
cp .env.example .env                        # set MSSQL_SA_PASSWORD
cp src/Wallet.Api/appsettings.Development.json.example \
   src/Wallet.Api/appsettings.Development.json   # put the same password in the connection string

# Option A: the whole system in Docker (no .NET needed)
docker compose --profile app up --build     # -> http://localhost:8080/swagger

# Option B: database in Docker, API on the host (fast dev loop)
docker compose up -d
dotnet run --project src/Wallet.Api

# Tests (need the database up)
docker compose up -d
dotnet test

# The execution-plan study
docker exec -i wallet-sqlserver /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -W \
  < db/perf/balance_plan_study.sql
```

## Repository layout

```
wallet/
  README.md
  PLAN.md                     build plan and milestones
  docker-compose.yml          SQL Server, and the API behind the "app" profile
  docs/                       schema.md, execution-plans/, adr/
  db/
    migrations/               numbered tables, procedures and indexes (single source of truth)
    perf/                     the execution-plan study
  src/
    Wallet.Api/               ASP.NET Core Web API
    Wallet.Core/              domain: Money, models, interfaces, errors
    Wallet.Infrastructure/    ADO.NET data access + migration runner
  tests/
    Wallet.UnitTests/         xUnit, no database
    Wallet.IntegrationTests/  xUnit against SQL Server (Docker)
```
