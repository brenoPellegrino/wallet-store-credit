using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wallet.IntegrationTests;

/// <summary>
/// Boots the API in-memory against a dedicated <c>WalletDb_Test</c> database and runs the
/// migrations (the app does this on startup). When SQL Server is not reachable the fixture stays
/// unavailable and the tests skip instead of failing, so the suite is green without a database.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string Server = "127.0.0.1,1433";
    private const string TestDatabase = "WalletDb_Test";

    private WebApplicationFactory<Program>? _factory;

    public bool Available { get; private set; }

    public string SkipReason { get; private set; } = string.Empty;

    public IServiceProvider Services =>
        _factory?.Services ?? throw new InvalidOperationException("The database is not available.");

    public async Task InitializeAsync()
    {
        var connectionString = ResolveConnectionString();
        if (connectionString is null)
        {
            SkipReason = "No SA password found. Set MSSQL_SA_PASSWORD or WALLET_TEST_CONNECTION, or create .env.";
            return;
        }

        if (!await ServerIsReachableAsync(connectionString))
        {
            SkipReason = $"SQL Server is not reachable at {Server}. Start it with 'docker compose up -d'.";
            return;
        }

        // Startup runs EnsureDatabaseExistsAsync + migrations against WalletDb_Test.
        _factory = new WalletApiFactory(connectionString);
        _ = _factory.Services;
        Available = true;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        return Task.CompletedTask;
    }

    private static string? ResolveConnectionString()
    {
        var explicitConnection = Environment.GetEnvironmentVariable("WALLET_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(explicitConnection))
        {
            return explicitConnection;
        }

        var password = Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD") ?? ReadPasswordFromEnvFile();
        if (string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        return new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = TestDatabase,
            UserID = "sa",
            Password = password,
            TrustServerCertificate = true,
            Encrypt = false,
        }.ConnectionString;
    }

    // Reads MSSQL_SA_PASSWORD from the repo's .env so `dotnet test` works right after `docker compose up`.
    private static string? ReadPasswordFromEnvFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var envPath = Path.Combine(dir.FullName, ".env");
            if (!File.Exists(envPath))
            {
                continue;
            }

            foreach (var line in File.ReadAllLines(envPath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("MSSQL_SA_PASSWORD=", StringComparison.Ordinal))
                {
                    return trimmed["MSSQL_SA_PASSWORD=".Length..].Trim();
                }
            }
        }

        return null;
    }

    private static async Task<bool> ServerIsReachableAsync(string connectionString)
    {
        var master = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
            ConnectTimeout = 3,
        }.ConnectionString;

        // SQL Server under emulation can take a while to accept logins after the container starts.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(master);
                await connection.OpenAsync();
                return true;
            }
            catch (SqlException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }

        return false;
    }

    private sealed class WalletApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;

        public WalletApiFactory(string connectionString) => _connectionString = connectionString;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["WalletDatabase:ConnectionString"] = _connectionString,
                }));
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
