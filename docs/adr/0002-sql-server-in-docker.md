# ADR 0002: SQL Server in Docker

Status: accepted (M2)

## Context

The plan assumed SQL Server running in DBngin on macOS. On this machine (Apple Silicon) DBngin
only offers PostgreSQL, MySQL, MariaDB and Redis. SQL Server has no native ARM build, so DBngin
cannot host it. The project's goal is specifically SQL Server depth (T-SQL, stored procedures,
execution-plan analysis), so switching engines was rejected.

## Decision

Run SQL Server 2022 in Docker, defined in `docker-compose.yml`. On arm64 the x64 image runs under
emulation (`platform: linux/amd64`). This is slower to pull and boot than a native image but is
fine for a development database.

Secrets are kept out of source control:

- `.env` (git-ignored) holds `MSSQL_SA_PASSWORD`, which Compose reads. `.env.example` is the template.
- `appsettings.Development.json` (git-ignored) holds the API connection string.
  `appsettings.Development.json.example` is the template.

The migration runner creates the target database if it is missing (it connects to `master`), so a
fresh container needs no manual `CREATE DATABASE`.

## Consequences

- Setup is `docker compose up -d`, then run the app or the tests. No extra database steps.
- Integration tests use a separate `WalletDb_Test` database and skip themselves when SQL Server is
  not reachable, so the suite is green without a running container.
- Alternatives if emulation ever becomes a problem: a remote or cloud SQL Server, or Azure SQL Edge
  (its ARM builds are discontinued, so it is not a reliable option today).
