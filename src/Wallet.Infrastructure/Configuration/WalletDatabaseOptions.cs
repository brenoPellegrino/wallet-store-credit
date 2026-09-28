namespace Wallet.Infrastructure.Configuration;

/// <summary>
/// Strongly typed database settings bound from the "WalletDatabase" configuration section.
/// </summary>
public sealed class WalletDatabaseOptions
{
    public const string SectionName = "WalletDatabase";

    /// <summary>
    /// Runtime SQL Server connection string, used by the API to call the stored procedures. It should
    /// point at the least-privilege application login (<c>wallet_app</c>), which only has EXECUTE on the
    /// wallet procedures. Keep the real password out of source control (use appsettings.Development.json
    /// or user secrets).
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Connection string for the privileged login that runs migrations. Migrations create the database,
    /// the tables, the stored procedures and the runtime login, so this points at an administrative login
    /// (<c>sa</c> in development). When left empty it falls back to <see cref="ConnectionString"/>, which
    /// keeps a single-login setup working.
    /// </summary>
    public string MigrationConnectionString { get; set; } = string.Empty;

    /// <summary>The migration connection string, or the runtime one when no separate migration login is configured.</summary>
    public string MigrationConnectionStringOrDefault =>
        string.IsNullOrWhiteSpace(MigrationConnectionString) ? ConnectionString : MigrationConnectionString;
}
