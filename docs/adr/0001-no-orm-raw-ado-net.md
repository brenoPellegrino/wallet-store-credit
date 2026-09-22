# ADR 0001: Raw ADO.NET everywhere, no ORM

Status: accepted (M2)

## Context

The original plan used a split: raw ADO.NET for the money mutations and EF Core for reads and
wallet creation, so the project could show "both levels". The one read that is actually
interesting is the balance query, an expiration-filtered per-currency aggregate with an
`OUTER APPLY` over the allocations. That query is also the centerpiece of the M4 execution-plan
study, which compares its plan before and after the covering index.

## Decision

Drop EF Core. Use raw ADO.NET for every path:

- Money mutations (credit, later debit and transfer) go through stored procedures called over ADO.NET.
- Reads (wallet lookup, balance, statement) are hand-written SQL over `SqlCommand`/`SqlDataReader`.

## Consequences

- The balance query stays hand-written and deterministic, so the plan study profiles our SQL and
  not an ORM's generated SQL.
- One data-access story instead of two: less to explain, and it matches the project's goal of
  proving SQL and ADO.NET depth.
- We give up the "ORM level" bullet. That is intentional: ticking it added nothing this project
  needs and split the design.
- The unused `Microsoft.EntityFrameworkCore.SqlServer` package was removed.
