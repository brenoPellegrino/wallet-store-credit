-- Migration 006: the least-privilege runtime login.
--
-- Migrations run as a privileged login (sa in development) because they create the database, the
-- tables and the stored procedures. The API itself must not have that power. This migration creates
-- a dedicated application login, wallet_app, and grants it only what the runtime data access in
-- WalletRepository actually uses: no DDL, no access to anything else.
--
-- The money ledger is protected: every credit and debit goes through a stored procedure, so
-- wallet_app gets EXECUTE on the three procedures but cannot INSERT/UPDATE/DELETE the
-- wallet_credits, wallet_debits or wallet_debit_allocations tables directly. It cannot touch
-- wallet_debits at all (that ledger is only ever read through usp_GetWalletStatement).
--
-- The reads and wallet creation are hand-written SQL rather than procedures, so wallet_app also
-- needs the matching table grants: SELECT on the tables those reads touch, plus INSERT on wallets
-- for CreateWalletAsync. It still holds no UPDATE or DELETE anywhere, matching the append-only design.
--
-- The password here is a development convenience and matches the committed sa dev password style. In
-- a real deployment, create this login out of band with a secret password and hand it to the API
-- through WalletDatabase:ConnectionString, while migrations keep using the privileged login.

IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'wallet_app')
    CREATE LOGIN wallet_app WITH PASSWORD = N'WalletApp!Dev2026';
GO

IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'wallet_app')
    CREATE USER wallet_app FOR LOGIN wallet_app;
GO

-- Money mutations: procedures only.
GRANT EXECUTE ON dbo.usp_CreditWallet       TO wallet_app;
GRANT EXECUTE ON dbo.usp_DebitWallet        TO wallet_app;
GRANT EXECUTE ON dbo.usp_GetWalletStatement TO wallet_app;
GO

-- Reads and wallet creation: hand-written SQL, so grant exactly the table access those paths use.
GRANT SELECT, INSERT ON dbo.wallets                 TO wallet_app;  -- GetWallet, GetBalances, transfer lock, CreateWallet
GRANT SELECT         ON dbo.wallet_credits          TO wallet_app;  -- GetBalances
GRANT SELECT         ON dbo.wallet_debit_allocations TO wallet_app; -- GetBalances
GO
