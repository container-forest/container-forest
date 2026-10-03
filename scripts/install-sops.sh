#!/bin/sh
# Installs sops and age-keygen, the tools the aggregator decrypts SOPS files and creates age keys with,
# after checking them against the checksums below. Used by the aggregator image and by CI.
#   scripts/install-sops.sh <directory> [amd64|arm64]
# To update: change a version, download both architectures and replace the checksums
# (sops publishes sops-v<version>.checksums.txt; for age, sha256sum the release archives).
set -eu

SOPS_VERSION=3.13.3
AGE_VERSION=1.3.2

dir=$1
arch=${2:-$(uname -m | sed 's/^x86_64$/amd64/; s/^aarch64$/arm64/')}
case $arch in
  amd64)
    sops_sha256=e5bec3346a873ae91d871550f3e698c1aad962aff462a080e40f25fde17fef6b
    age_sha256=cbe24006683f8eb669266162894b9a522a1af52f2665fbc63a4bb032ed26ac10
    ;;
  arm64)
    sops_sha256=53b0abacd38ef1b12a66d6c100956691b9cefce018d91f81e73ddf7438b94d77
    age_sha256=6b8dc4333c53a5a57c9e5834e3a48f92605d7154014cd07269ff3327db5d37f4
    ;;
  *)
    echo "install-sops.sh: unsupported architecture '$arch'" >&2
    exit 1
    ;;
esac

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

curl -fsSL -o "$tmp/sops" "https://github.com/getsops/sops/releases/download/v$SOPS_VERSION/sops-v$SOPS_VERSION.linux.$arch"
curl -fsSL -o "$tmp/age.tar.gz" "https://github.com/FiloSottile/age/releases/download/v$AGE_VERSION/age-v$AGE_VERSION-linux-$arch.tar.gz"
echo "$sops_sha256  $tmp/sops" | sha256sum -c -
echo "$age_sha256  $tmp/age.tar.gz" | sha256sum -c -

tar -xzf "$tmp/age.tar.gz" -C "$tmp" age/age-keygen
mkdir -p "$dir"
install -m 0755 "$tmp/sops" "$tmp/age/age-keygen" "$dir/"
