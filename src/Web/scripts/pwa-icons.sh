#!/usr/bin/env bash
# Renders the PWA icons in public/icons from the SVGs in pwa/ and public/logo.svg with headless Chrome
# (no image tools needed). Run from src/Web after changing the logo; the PNGs are committed.
set -euo pipefail
cd "$(dirname "$0")/.."
chrome="${CHROME:-/Applications/Google Chrome.app/Contents/MacOS/Google Chrome}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

render() { # <svg> <size> <png>
  local svg size=$2 out=$3
  svg="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
  printf '<!doctype html><style>html,body{margin:0;background:transparent}img{display:block;width:%spx;height:%spx}</style><img src="file://%s">' \
    "$size" "$size" "$svg" > "$work/page.html"
  "$chrome" --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 \
    --default-background-color=00000000 --window-size="$size,$size" \
    --screenshot="$out" "file://$work/page.html" 2>/dev/null
}

render public/logo.svg 192 public/icons/icon-192.png
render public/logo.svg 512 public/icons/icon-512.png
render pwa/logo-maskable.svg 512 public/icons/maskable-512.png
render pwa/logo-full-bleed.svg 180 public/icons/apple-touch-icon.png
