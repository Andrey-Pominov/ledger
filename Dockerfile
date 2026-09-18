FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Ledger.slnx ./
COPY src/Ledger.Core/Ledger.Core.csproj src/Ledger.Core/
COPY src/Ledger.Api/Ledger.Api.csproj   src/Ledger.Api/
RUN dotnet restore src/Ledger.Api/Ledger.Api.csproj
COPY db/ db/
COPY src/ src/
RUN dotnet publish src/Ledger.Api/Ledger.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Ledger.Api.dll"]
