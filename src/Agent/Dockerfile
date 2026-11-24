# syntax=docker/dockerfile:1
# Image: container-forest-agent. Build from the repository root:
#   docker build -f src/Agent/Dockerfile -t container-forest-agent .

# The build stage runs on the builder's platform; the output is portable IL, so one build serves amd64 and arm64.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from the files that define dependencies only, so code changes keep the restore layer cached.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Contracts/ src/Contracts/
COPY src/Agent/ContainerForest.Agent.csproj src/Agent/
RUN dotnet restore src/Agent/ContainerForest.Agent.csproj

COPY src/Agent/ src/Agent/
RUN dotnet publish src/Agent/ContainerForest.Agent.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0
LABEL org.opencontainers.image.title="container-forest-agent" \
      org.opencontainers.image.source="https://github.com/container-forest/container-forest"

# Stacks are deployed with the docker CLI and its compose plugin, which talk to the host's daemon through the socket.
# Only the client is installed: the daemon is the host's. Packages come from Docker's apt repository, because
# the distribution's docker.io lags behind and has no compose v2 plugin. The repository follows the base image's
# distribution (ID in os-release): the .NET 10 images are Ubuntu 24.04, earlier ones were Debian.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && install -m 0755 -d /etc/apt/keyrings \
    && . /etc/os-release \
    && curl -fsSL "https://download.docker.com/linux/${ID}/gpg" -o /etc/apt/keyrings/docker.asc \
    && chmod a+r /etc/apt/keyrings/docker.asc \
    && echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/${ID} ${VERSION_CODENAME} stable" \
        > /etc/apt/sources.list.d/docker.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends docker-ce-cli docker-compose-plugin \
    && apt-get purge -y curl \
    && apt-get autoremove -y \
    && rm -rf /var/lib/apt/lists/*

# The content root must be the application directory, or appsettings.json is not found.
WORKDIR /app
COPY --from=build /app .

# Runs as root: the agent talks to the host's Docker socket, whose group id differs between hosts,
# and access to that socket is root on the host anyway. No EXPOSE: the agent opens no inbound ports.
ENV DOTNET_ENVIRONMENT=Production \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

ENTRYPOINT ["dotnet", "ContainerForest.Agent.dll"]
