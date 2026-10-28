# Operator console as a container. It is a desktop app, so it draws on the host's X server:
# see the "Run in Docker" section of IotMonitor.Operator/README.md for the DISPLAY and xhost setup.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Monitor.Operator/Monitor.Operator.csproj Monitor.Operator/
RUN dotnet restore Monitor.Operator
COPY Monitor.Operator Monitor.Operator
RUN dotnet publish Monitor.Operator -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
# Avalonia on Linux loads these at run time (X11 client libraries, fontconfig for Skia) and needs a font.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      libx11-6 libice6 libsm6 libxext6 libxcursor1 libxrandr2 libxi6 \
      libfontconfig1 libfreetype6 libgl1 fonts-dejavu-core \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# The API's address inside the compose network; override with MONITOR_API or a command-line argument.
ENV MONITOR_API=http://api:8080
ENTRYPOINT ["dotnet", "Monitor.Operator.dll"]
