FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/Monitor.Core/*.csproj src/Monitor.Core/
COPY src/Monitor.Ingestion/*.csproj src/Monitor.Ingestion/
RUN dotnet restore src/Monitor.Ingestion
COPY src ./src
RUN dotnet publish src/Monitor.Ingestion -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENTRYPOINT ["dotnet", "Monitor.Ingestion.dll"]