using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Wallet.Infrastructure.Data;

namespace Wallet.Infrastructure.Migrations;

/// <summary>
/// A hand-rolled ADO.NET migration runner. Migration scripts are embedded in this assembly
/// (linked from <c>db/migrations</c>), named with a numeric prefix such as
/// <c>001_create_tables.sql</c>. Each unapplied script runs inside its own
/// <see cref="SqlTransaction"/>, then a row is written to <c>__schema_versions</c> with the
/// script's SHA-256 checksum. A script that was already applied is skipped, but its checksum is
/// re-verified so an accidental edit to a shipped migration fails loudly.
/// </summary>
public sealed partial class DatabaseMigrator : IDatabaseMigrator
{
    private const string ResourcePrefix = "Wallet.Migrations.";

    private readonly ISqlConnectionFactory _connectionFactory;

    public DatabaseMigrator(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<string>> MigrateAsync(CancellationToken cancellationToken = default)
    {
        var scripts = LoadScripts();

        await WaitForServerAsync(cancellationToken);
        await EnsureDatabaseExistsAsync(cancellationToken);

        await using var connection = await _connectionFactory.CreateOpenAsync(cancellationToken);
        await EnsureVersionsTableAsync(connection, cancellationToken);
        var applied = await LoadAppliedAsync(connection, cancellationToken);

        var appliedNow = new List<string>();
        foreach (var script in scripts)
        {
            if (applied.TryGetValue(script.Version, out var recordedChecksum))
            {
                if (!recordedChecksum.SequenceEqual(script.Checksum))
                {
                    throw new MigrationException(
                        $"Migration {script.Version} '{script.Name}' was modified after it was applied. " +
                        "Migrations are immutable; add a new migration instead of editing this one.");
                }

                continue;
            }

            await ApplyAsync(connection, script, cancellationToken);
            appliedNow.Add(script.Name);
        }

        return appliedNow;
    }

    /// <summary>
    /// Waits for the server to accept connections before migrating. A just-started SQL Server
    /// container (especially under emulation) reports its port open before it accepts logins, so
    /// the API would otherwise fail its startup migration on the first boot of the stack.
    /// </summary>
    private async Task WaitForServerAsync(CancellationToken cancellationToken)
    {
        var master = new SqlConnectionStringBuilder(_connectionFactory.ConnectionString)
        {
            InitialCatalog = "master",
            ConnectTimeout = 5,
        }.ConnectionString;

        const int maxAttempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var connection = new SqlConnection(master);
                await connection.OpenAsync(cancellationToken);
                return;
            }
            catch (SqlException) when (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Creates the target database if it does not exist yet, by connecting to <c>master</c>. This
    /// lets a fresh SQL Server (for example a just-started Docker container) be migrated without a
    /// manual "CREATE DATABASE" step.
    /// </summary>
    private async Task EnsureDatabaseExistsAsync(CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder(_connectionFactory.ConnectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName) ||
            databaseName.Equals("master", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!SafeDatabaseName().IsMatch(databaseName))
        {
            throw new MigrationException(
                $"Database name '{databaseName}' is not a simple identifier; refusing to create it.");
        }

        builder.InitialCatalog = "master";
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"IF DB_ID(N'{databaseName}') IS NULL CREATE DATABASE [{databaseName}];";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ApplyAsync(SqlConnection connection, Script script, CancellationToken cancellationToken)
    {
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var batch in SplitIntoBatches(script.Sql))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = batch;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT dbo.__schema_versions (version, script_name, checksum) " +
                    "VALUES (@version, @script_name, @checksum);";
                record.Parameters.AddWithValue("@version", script.Version);
                record.Parameters.AddWithValue("@script_name", script.Name);
                record.Parameters.AddWithValue("@checksum", script.Checksum);
                await record.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not MigrationException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new MigrationException($"Migration {script.Version} '{script.Name}' failed and was rolled back.", ex);
        }
    }

    private static async Task EnsureVersionsTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            IF OBJECT_ID('dbo.__schema_versions', 'U') IS NULL
            CREATE TABLE dbo.__schema_versions
            (
                version        INT           NOT NULL CONSTRAINT PK___schema_versions PRIMARY KEY,
                script_name    NVARCHAR(260) NOT NULL,
                applied_at_utc DATETIME2(3)  NOT NULL CONSTRAINT DF___schema_versions_applied_at DEFAULT SYSUTCDATETIME(),
                checksum       VARBINARY(32) NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Dictionary<int, byte[]>> LoadAppliedAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var applied = new Dictionary<int, byte[]>();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version, checksum FROM dbo.__schema_versions;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = reader.GetInt32(0);
            var checksum = reader.IsDBNull(1) ? Array.Empty<byte>() : (byte[])reader[1];
            applied[version] = checksum;
        }

        return applied;
    }

    private static IReadOnlyList<Script> LoadScripts()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var scripts = new List<Script>();

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var fileName = resourceName[ResourcePrefix.Length..];
            var match = VersionPrefix().Match(fileName);
            if (!match.Success)
            {
                throw new MigrationException(
                    $"Migration script '{fileName}' must start with a numeric version prefix, for example '001_'.");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var streamReader = new StreamReader(stream, Encoding.UTF8);
            var sql = streamReader.ReadToEnd();

            scripts.Add(new Script(
                int.Parse(match.Groups[1].Value),
                fileName,
                sql,
                SHA256.HashData(Encoding.UTF8.GetBytes(sql))));
        }

        return scripts.OrderBy(s => s.Version).ToList();
    }

    /// <summary>
    /// Splits a script on lines containing only a <c>GO</c> batch separator. <c>GO</c> is a client
    /// directive, not T-SQL, so ADO.NET cannot send it; statements such as <c>CREATE OR ALTER
    /// PROCEDURE</c> that must be the first statement in a batch rely on this split.
    /// </summary>
    private static IEnumerable<string> SplitIntoBatches(string sql)
    {
        var batches = BatchSeparator().Split(sql);
        foreach (var batch in batches)
        {
            if (!string.IsNullOrWhiteSpace(batch))
            {
                yield return batch;
            }
        }
    }

    [GeneratedRegex(@"^\s*(\d+)")]
    private static partial Regex VersionPrefix();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatchSeparator();

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")]
    private static partial Regex SafeDatabaseName();

    private sealed record Script(int Version, string Name, string Sql, byte[] Checksum);
}
