# syntax=docker/dockerfile:1
# Image: container-forest-aggregator. Build from the repository root:
#   docker build -f src/Aggregator/Api/Dockerfile -t container-forest-aggregator .

# The build stage runs on the builder's platform; the output is portable IL, so one build serves amd64 and arm64.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the files that define dependencies only, so code changes keep the restore layer cached.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/ src/Contracts/
COPY src/Aggregator/Domain/ContainerForest.Aggregator.Domain.csproj src/Aggregator/Domain/
COPY src/Aggregator/Application/ContainerForest.Aggregator.Application.csproj src/Aggregator/Application/
COPY src/Aggregator/Infrastructure/ContainerForest.Aggregator.Infrastructure.csproj src/Aggregator/Infrastructure/
COPY src/Aggregator/Api/ContainerForest.Aggregator.Api.csproj src/Aggregator/Api/
RUN dotnet restore src/Aggregator/Api/ContainerForest.Aggregator.Api.csproj

COPY src/Aggregator/ src/Aggregator/
# The OpenAPI document is committed and checked by CI; generating it here would only start the host once more.
RUN dotnet publish src/Aggregator/Api/ContainerForest.Aggregator.Api.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false -p:OpenApiGenerateDocuments=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
LABEL org.opencontainers.image.title="container-forest-aggregator" \
      org.opencontainers.image.source="https://github.com/lepterion/container-forest"

# The content root must be the application directory, or appsettings.json is not found.
WORKDIR /app
COPY --from=build /app .

# Everything the aggregator keeps lives in /data: the database, the Data Protection key ring (without it stored
# secrets cannot be decrypted, so it is backed up with the database) and the git clones. A named volume mounted
# there inherits the directory's owner, so the unprivileged user can write to it.
RUN mkdir /data && chown "$APP_UID" /data
VOLUME /data
# ASPNETCORE_HTTP_PORTS of the base image is cleared: the ports come from Kestrel's endpoints in appsettings.json.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS= \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    CF_CONNECTIONSTRINGS__AGGREGATOR="Data Source=/data/aggregator.db" \
    CF_DATAPROTECTION__KEYDIRECTORY=/data/data-protection-keys \
    CF_GIT__CACHEDIRECTORY=/data/git-cache

USER $APP_UID
# 5080: REST and SignalR for the panel; 5081: gRPC (HTTP/2) for agents and the bot. Both plain HTTP: TLS ends at
# the reverse proxy in front (deploy/aggregator).
EXPOSE 5080 5081
ENTRYPOINT ["dotnet", "ContainerForest.Aggregator.Api.dll"]
