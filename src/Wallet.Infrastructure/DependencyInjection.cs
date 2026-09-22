using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

        services.AddSingleton<ISqlConnectionFactory, SqlConnectionFactory>();
        services.AddSingleton<IDatabaseMigrator, DatabaseMigrator>();
        services.AddScoped<IWalletRepository, WalletRepository>();

        return services;
    }
}
