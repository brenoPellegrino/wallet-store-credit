namespace Wallet.Infrastructure.Configuration;

/// <summary>
/// Strongly typed database settings bound from the "WalletDatabase" configuration section.
/// </summary>
public sealed class WalletDatabaseOptions
{
    public const string SectionName = "WalletDatabase";

    /// <summary>
    /// SQL Server connection string. In development it points at the local DBngin SQL Server instance.
    /// Keep the real password out of source control (use appsettings.Development.json or user secrets).
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;
}
