namespace Wallet.Infrastructure.Migrations;

/// <summary>
/// Applies the numbered SQL migration scripts to the database, in order, exactly once each.
/// </summary>
public interface IDatabaseMigrator
{
    /// <summary>
    /// Applies every migration that has not yet been recorded in <c>__schema_versions</c>, each in
    /// its own transaction. Returns the migrations applied by this call (empty when up to date).
    /// </summary>
    /// <exception cref="MigrationException">A script failed or was edited after being applied.</exception>
    Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken = default);
}
