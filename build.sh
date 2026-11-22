#!/usr/bin/env bash
# Builds, packs and validates the plugin into ./artifacts.
#   ./build.sh          build the .macroDeckPlugin
#   ./build.sh --run    run it live against the open Macro Deck (approve the pairing prompt)
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
export PATH="$PATH:$HOME/.dotnet/tools"

command -v macrodeck-plugin >/dev/null \
  || dotnet tool install --global MacroDeck.Plugin.Cli \
  || dotnet tool install --global MacroDeck.Plugin.Cli --prerelease

if [ "${1:-}" = "--run" ]; then
  exec macrodeck-plugin run --project src/AudioSwitcher/AudioSwitcher.csproj
fi

dotnet build src/AudioSwitcher -c Release
mkdir -p artifacts
( cd src/AudioSwitcher && macrodeck-plugin build --output ../../artifacts )
artifact="$(ls -t artifacts/*.macroDeckPlugin | head -n1)"
macrodeck-plugin validate --artifact "$artifact"
echo "Built: $artifact"
echo "Install it: Macro Deck -> Integrations -> Add -> Install from file..."
