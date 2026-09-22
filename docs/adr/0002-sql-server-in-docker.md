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

## The whole system in Docker

The API also has a Dockerfile and an `api` service in Compose, behind the `app` profile, so someone
who hits the same "no SQL Server" wall (or who does not have the .NET SDK) can run everything with
one command:

- `docker compose up -d` starts only the database. This is the default, so the fast local loop
  (`dotnet run` against the container) is unchanged.
- `docker compose --profile app up --build` starts the database and the API together, reachable at
  `http://localhost:8080/swagger`. The API image is native to the host architecture; only SQL Server
  is emulated.

The API waits for the database to accept logins (a bounded retry in the migration runner), because
the container's TCP healthcheck goes green before SQL Server is ready for connections. The
connection string is passed as an environment variable pointing at the `sqlserver` service; the
password stays in `.env` and is never baked into the image.
