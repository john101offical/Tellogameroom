# Build context: the repository root.
#   docker build -f deploy/docker/Dockerfile -t tellogameroom-server .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so this layer is cached until a project file changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/TelloGameRoom.Core/TelloGameRoom.Core.csproj src/TelloGameRoom.Core/
COPY src/TelloGameRoom.Server/TelloGameRoom.Server.csproj src/TelloGameRoom.Server/
RUN dotnet restore src/TelloGameRoom.Server/TelloGameRoom.Server.csproj

COPY src/TelloGameRoom.Core/ src/TelloGameRoom.Core/
COPY src/TelloGameRoom.Server/ src/TelloGameRoom.Server/
RUN dotnet publish src/TelloGameRoom.Server/TelloGameRoom.Server.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# fontconfig: PDF export. tzdata: branch time zones for reports. curl: container health check.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 tzdata curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data && chown $APP_UID /data

WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__DataDirectory=/data \
    DOTNET_gcServer=0
VOLUME /data
EXPOSE 8080
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "TelloGameRoom.Server.dll"]
