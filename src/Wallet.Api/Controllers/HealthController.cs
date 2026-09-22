using Microsoft.AspNetCore.Mvc;
using Wallet.Infrastructure.Data;

namespace Wallet.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController : ControllerBase
{
    private readonly ISqlConnectionFactory _connectionFactory;

    public HealthController(ISqlConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    /// <summary>Liveness: the API process is up.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok" });

    /// <summary>Readiness: the API can open a SQL Server connection and run a trivial query.</summary>
    [HttpGet("db")]
    public async Task<IActionResult> Database(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _connectionFactory.CreateOpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1;";
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return Ok(new { status = "ok", database = "reachable", probe = result });
        }
        catch (Exception ex)
        {
            return StatusCode(503, new { status = "error", database = "unreachable", detail = ex.Message });
        }
    }
}
