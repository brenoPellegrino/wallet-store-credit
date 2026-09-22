# Wallet / Store-Credit System, Build Plan

A standalone Wallet / store-credit system. A C# / .NET 8 Web API backed by SQL Server.
The goal is a portfolio project that proves depth in SQL, ADO.NET and stored procedures.

## Goals this project must prove

- Raw ADO.NET on the core money paths: `SqlConnection`, `SqlCommand`, `SqlDataReader`, `SqlTransaction`.
- Stored procedures for credit, debit and a statement/report endpoint.
- SQL Server depth: a well-designed schema (keys, constraints, data types), indexes including a covering index, and execution-plan analysis documented in the README.
- Transactions with commit and rollback so a balance is never left half-updated (ACID in practice).
- Idempotency and concurrency safety: a ledger keyed by a unique `EventId` so repeated or concurrent requests never double-spend.
- Schema versioning that shows both the raw-SQL and ORM levels.
- xUnit tests on the money logic.
- A strong README as the interview centerpiece.

## Key decisions

- **Stack**: C# / .NET 8, ASP.NET Core Web API (controllers), SQL Server.
- **Data access split**: raw ADO.NET for every money mutation (credit, debit, transfer). EF Core for reads and wallet creation. This shows both levels without letting the ORM touch the critical money paths.
- **Migrations**: a hand-rolled ADO.NET migration runner (a `__SchemaVersions` table plus numbered `.sql` scripts run inside a `SqlTransaction`). This adds more raw SQL and ADO.NET to the showcase. Stored procedures and indexes are versioned as SQL scripts. An optional small EF Core migrations demo can be added in M6 if we want that specific bullet.
- **Money type**: `DECIMAL(19,4)` in the database and `decimal` in C#, never `float`.
- **Local database**: SQL Server running in DBngin on macOS. Connection string lives in `appsettings.Development.json` (or user secrets). LocalDB is not used because it is Windows only.

## Way of working

- Build the MVP first, then layer on the rest.
- One milestone at a time. After each milestone I stop for review before starting the next.
- Definition of done for every milestone: the solution builds, all tests pass, and the relevant docs (README, `docs/schema.md` or an ADR) are updated.
- Decisions are recorded here in `PLAN.md`. Schema decisions live in `docs/schema.md`. Larger decisions get a short ADR in `docs/adr/`.

## Milestones

- **M0 Scaffold (done)**: solution and projects, ADO.NET connection factory, health endpoints, configuration, this plan.
- **M1 Schema design (design only, no code)**: agree tables, keys, columns, data types, constraints and indexes. Document the reasoning in `docs/schema.md`. Reviewed and approved before any implementation.
- **M2 MVP implementation**: the ADO.NET migration runner, the tables from M1, `usp_CreditWallet`, create and get wallet via EF, credit and debit via ADO.NET, `EventId` idempotency, first xUnit tests (Money type plus an idempotent-replay integration test).
- **M3 Debit proc and statement**: `usp_DebitWallet` with insufficient-funds handling, `usp_GetWalletStatement`, the covering index, tests for overdraft and idempotent debit.
- **M4 Concurrency and performance**: a parallel double-spend test, then the execution-plan study (before and after the covering index) captured in `docs/execution-plans/`.
- **M5 Transfer**: a client-side `SqlTransaction` across two wallets, with commit and rollback tests.
- **M6 README and polish**: the centerpiece README, ADRs, input validation and error handling, final review.

## Repository layout

```
wallet/
  Wallet.sln
  PLAN.md
  docs/            schema.md, execution-plans/, adr/
  db/              migrations/, procedures/, perf/
  src/
    Wallet.Api/            ASP.NET Core Web API
    Wallet.Core/           domain: entities, Money type, interfaces, errors (no infrastructure)
    Wallet.Infrastructure/ EF Core reads + ADO.NET money paths + migration runner
  tests/
    Wallet.UnitTests/          xUnit, no database
    Wallet.IntegrationTests/   xUnit against SQL Server (DBngin)
```
