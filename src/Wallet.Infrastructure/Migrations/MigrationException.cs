namespace Wallet.Infrastructure.Migrations;

/// <summary>
/// Raised when the migration runner cannot apply the schema: a script failed, or a script that
/// was already applied has since been edited (its checksum no longer matches what was recorded).
/// </summary>
public sealed class MigrationException : Exception
{
    public MigrationException(string message) : base(message)
    {
    }

    public MigrationException(string message, Exception inner) : base(message, inner)
    {
    }
}
