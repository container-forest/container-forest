# syntax=docker/dockerfile:1
# Image: container-forest-discord-bot. Build from the repository root:
#   docker build -f src/DiscordBot/Dockerfile -t container-forest-discord-bot .

# The build stage runs on the builder's platform; the output is portable IL, so one build serves amd64 and arm64.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the files that define dependencies only, so code changes keep the restore layer cached.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/ src/Contracts/
COPY src/DiscordBot/ContainerForest.DiscordBot.csproj src/DiscordBot/
RUN dotnet restore src/DiscordBot/ContainerForest.DiscordBot.csproj

COPY src/DiscordBot/ src/DiscordBot/
RUN dotnet publish src/DiscordBot/ContainerForest.DiscordBot.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
LABEL org.opencontainers.image.title="container-forest-discord-bot" \
      org.opencontainers.image.source="https://github.com/container-forest/container-forest"

# The content root must be the application directory, or appsettings.json is not found.
WORKDIR /app
COPY --from=build /app .

# The bot only makes outbound connections (Discord, the aggregator) and writes nothing to disk,
# so it runs as the image's unprivileged user. No EXPOSE: it opens no inbound ports.
USER $APP_UID
ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

ENTRYPOINT ["dotnet", "ContainerForest.DiscordBot.dll"]
