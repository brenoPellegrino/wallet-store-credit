using Wallet.Infrastructure;
using Wallet.Infrastructure.Migrations;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Infrastructure: ADO.NET connection factory, migration runner and the wallet repository.
builder.Services.AddWalletInfrastructure(builder.Configuration);

var app = builder.Build();

// Apply any pending schema migrations before serving traffic.
using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<IDatabaseMigrator>();
    var applied = await migrator.MigrateAsync();
    if (applied.Count > 0)
    {
        app.Logger.LogInformation("Applied {Count} migration(s): {Scripts}", applied.Count, string.Join(", ", applied));
    }
}

// HTTP pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();

// Exposed so the integration test project can reference the API host with WebApplicationFactory<Program>.
public partial class Program { }
