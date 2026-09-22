# Multi-stage build for the Wallet API. Build context is the repository root
# (see docker-compose.yml), so the whole solution and db/migrations are available.
# The SDK tag matches global.json (8.0.404). The API image is native to the host
# architecture; only SQL Server runs emulated.

FROM mcr.microsoft.com/dotnet/sdk:8.0.404 AS build
WORKDIR /src

# Restore first, using only the project files so this layer caches until they change.
COPY global.json ./
COPY Wallet.sln ./
COPY src/Wallet.Api/Wallet.Api.csproj src/Wallet.Api/
COPY src/Wallet.Core/Wallet.Core.csproj src/Wallet.Core/
COPY src/Wallet.Infrastructure/Wallet.Infrastructure.csproj src/Wallet.Infrastructure/
RUN dotnet restore src/Wallet.Api/Wallet.Api.csproj

# Copy the rest and publish. db/migrations is embedded into the Infrastructure assembly.
COPY src/ src/
COPY db/ db/
RUN dotnet publish src/Wallet.Api/Wallet.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Wallet.Api.dll"]
