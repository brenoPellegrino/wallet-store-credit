using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Wallet.Core.Abstractions;
using Wallet.Infrastructure.Configuration;
using Wallet.Infrastructure.Data;
using Wallet.Infrastructure.Migrations;

namespace Wallet.Infrastructure;

/// <summary>
/// Registration for the infrastructure layer: the ADO.NET connection factory, the migration
/// runner and the wallet repository.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddWalletInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<WalletDatabaseOptions>(
            configuration.GetSection(WalletDatabaseOptions.SectionName));

        // Runtime data access (the repository and the health probe) uses the least-privilege
        // application login, which only has EXECUTE on the wallet stored procedures.
        services.AddSingleton<ISqlConnectionFactory>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WalletDatabaseOptions>>().Value;
            return new SqlConnectionFactory(options.ConnectionString);
        });

        // Migrations run DDL and create the runtime login, so they connect with the privileged
        // migration login (falling back to the runtime one when none is configured separately).
        services.AddSingleton<IDatabaseMigrator>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<WalletDatabaseOptions>>().Value;
            var migrationFactory = new SqlConnectionFactory(options.MigrationConnectionStringOrDefault);
            return new DatabaseMigrator(migrationFactory);
        });

        services.AddScoped<IWalletRepository, WalletRepository>();

        return services;
    }
}
