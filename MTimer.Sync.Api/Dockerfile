FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["MTimer.Sync.Contracts/MTimer.Sync.Contracts.csproj", "MTimer.Sync.Contracts/"]
COPY ["MTimer.Sync.Api/MTimer.Sync.Api.csproj", "MTimer.Sync.Api/"]
RUN dotnet restore "MTimer.Sync.Api/MTimer.Sync.Api.csproj"

COPY . .
RUN dotnet publish "MTimer.Sync.Api/MTimer.Sync.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:5124
ENV MTIMER_SYNC_DATA_DIR=/data/mtimer

EXPOSE 5124
VOLUME ["/data/mtimer"]

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MTimer.Sync.Api.dll"]
