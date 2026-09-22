using Wallet.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Infrastructure: ADO.NET connection factory today, repositories and the migration runner later.
builder.Services.AddWalletInfrastructure(builder.Configuration);

var app = builder.Build();

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
