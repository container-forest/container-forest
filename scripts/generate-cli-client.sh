#!/usr/bin/env sh
# Regenerates the forest CLI's API client (src/Cli/Api/Generated) from the committed OpenAPI document with Kiota
# (ADR-0008). CI runs it and fails when the committed client differs. Run from anywhere: dotnet tool restore first.
set -eu
cd "$(dirname "$0")/.."

# Left out: endpoints the CLI never calls. Webhooks and deploy hooks are for git servers and CI; signing in with a
# password, passkeys, one's own signing keys (ADR-0011), the Discord link and the approval of a CLI login need a
# session in the panel, which the CLI never has.
dotnet kiota generate \
  --language CSharp \
  --openapi src/Aggregator/Api/openapi/ContainerForest.Aggregator.Api.json \
  --output src/Cli/Api/Generated \
  --class-name ForestApiClient \
  --namespace-name ContainerForest.Cli.Api.Generated \
  --exclude-backward-compatible \
  --clean-output \
  --log-level Error \
  --exclude-path "/api/webhooks/**" \
  --exclude-path "/api/deploy-hooks/**" \
  --exclude-path "/api/auth/**" \
  --exclude-path "/api/audit" \
  --exclude-path "/api/me/discord" \
  --exclude-path "/api/me/passkeys" \
  --exclude-path "/api/me/signing-keys" \
  --exclude-path "/api/me/signing-keys/**" \
  --exclude-path "/api/me/passkeys/**" \
  --exclude-path "/api/me/discord/**" \
  --exclude-path "/api/cli/authorizations/{userCode}/**" \
  --exclude-path "/api/cli/authorizations/{userCode}"
