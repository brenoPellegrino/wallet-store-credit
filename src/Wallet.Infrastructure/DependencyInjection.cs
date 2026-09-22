using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wallet.Infrastructure.Configuration;
using Wallet.Infrastructure.Data;

namespace Wallet.Infrastructure;

/// <summary>
/// Registration for the infrastructure layer (ADO.NET connection factory today, repositories and the migration runner later).
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

        return services;
    }
}
