#!/usr/bin/env bash
# Builds the Audio Device Switcher plugin for Macro Deck 3.
#
# It scaffolds a fresh project from the official Macro Deck plugin template (so the csproj,
# manifest and macrodeck-build.json always match your installed SDK/CLI), drops this plugin's
# source into it, then packs a .macroDeckPlugin you install with
#   Macro Deck -> Integrations -> Add -> Install from file...
#
# Usage: ./build.sh            (output goes to ./build)
#        ./build.sh --run      (also runs it live against the open Macro Deck for debugging)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$HERE/build"
PLUGIN_ID="com.benatkins.audio-switcher"
PLUGIN_NAME="Audio Device Switcher"
PUBLISHER="Ben Atkins"
PROJECT="AudioSwitcher"

export PATH="$PATH:$HOME/.dotnet/tools:$HOME/.dotnet"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

step() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
die()  { printf '\n\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

command -v dotnet >/dev/null || die "dotnet not found. Install the .NET 10 SDK (https://dot.net)."
dotnet --list-sdks | grep -q '^10\.' || die ".NET 10 SDK not found (have: $(dotnet --list-sdks | tr '\n' ' '))."
command -v python3 >/dev/null || die "python3 is needed for the project edits."
command -v pactl >/dev/null || echo "warning: pactl not found - the plugin needs it at runtime (package pulseaudio-utils / libpulse)."

if ! command -v macrodeck-plugin >/dev/null; then
  step "Installing the Macro Deck plugin CLI"
  dotnet tool install --global MacroDeck.Plugin.Cli \
    || dotnet tool install --global MacroDeck.Plugin.Cli --prerelease \
    || die "Could not install MacroDeck.Plugin.Cli"
fi

step "Scaffolding from the official template"
rm -rf "$WORK"
mkdir -p "$WORK"
( cd "$WORK" && macrodeck-plugin new --name "$PLUGIN_NAME" --id "$PLUGIN_ID" \
    --publisher "$PUBLISHER" --project-name "$PROJECT" --yes )

CSPROJ="$(find "$WORK" -name "$PROJECT.csproj" -not -path '*/tests/*' | head -n1)"
[ -n "$CSPROJ" ] || die "The template did not produce $PROJECT.csproj"
SRC="$(dirname "$CSPROJ")"
ROOT="$(cd "$SRC/../.." && pwd)"
echo "project: $SRC"

# The template's namespace is what the generated Strings class lives in; match it.
TEMPLATE_NS="$(grep -hoE '^namespace [A-Za-z0-9_.]+' "$SRC"/*.cs 2>/dev/null | head -n1 | awk '{print $2}')"
TEMPLATE_NS="${TEMPLATE_NS:-$PROJECT}"
echo "namespace: $TEMPLATE_NS"

step "Replacing the template's example code with the audio plugin"
grep -lE 'class (PluginIntegration|LogMessageAction)\b' "$SRC"/*.cs | xargs -r rm -f || true
rm -f "$SRC"/Localization/Strings.*.resx
rm -rf "$ROOT/tests"
cp -r "$HERE/plugin-src/." "$SRC/"
if [ "$TEMPLATE_NS" != "AudioSwitcher" ]; then
  find "$SRC" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print0 | xargs -0 sed -i \
    -e "s/^namespace AudioSwitcher/namespace $TEMPLATE_NS/" \
    -e "s/^using AudioSwitcher\./using $TEMPLATE_NS./"
fi

# Use the git remote (if any) as the manifest's repository link.
REPO_URL="$(git -C "$HERE" remote get-url origin 2>/dev/null | sed -E 's#^git@([^:]+):#https://\1/#; s#\.git$##' || true)"

python3 - "$CSPROJ" "$ROOT" "$PLUGIN_ID" "$REPO_URL" <<'PY'
import json, pathlib, re, sys
csproj, root, plugin_id = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2]), sys.argv[3]
repo_url = sys.argv[4] if len(sys.argv) > 4 else ""

def add_ui_reference(path, element):
    text = path.read_text()
    if f'Include="MacroDeck.Ui"' in text:
        return False
    match = re.search(rf'<{element}\s+Include="MacroDeck\.Plugin\.Hosting"([^>]*?)/?>', text)
    if not match:
        return False
    attrs = match.group(1).rstrip('/').rstrip()
    line = f'<{element} Include="MacroDeck.Ui"{attrs} />'
    text = text[:match.start()] + line + "\n    " + text[match.start():]
    path.write_text(text)
    return True

# The widget uses Macro Deck's UI framework (MacroDeck.Ui), which the template may not reference.
print("csproj MacroDeck.Ui added:", add_ui_reference(csproj, "PackageReference"))
props = root / "Directory.Packages.props"
if props.exists():
    print("Directory.Packages.props MacroDeck.Ui added:", add_ui_reference(props, "PackageVersion"))

# Make sure the language features this code relies on are on, and that analyzer
# suggestions (e.g. about literal device names) never fail the build.
text = csproj.read_text()
wanted = {"Nullable": "enable", "ImplicitUsings": "enable", "TreatWarningsAsErrors": "false"}
extra = "".join(f"\n    <{k}>{v}</{k}>" for k, v in wanted.items() if f"<{k}>" not in text or k == "TreatWarningsAsErrors")
text = re.sub(r"<{0}>[^<]*</{0}>".format("TreatWarningsAsErrors"), "", text)
text = text.replace("<PropertyGroup>", "<PropertyGroup>" + extra, 1)
csproj.write_text(text)

# Describe the plugin and declare that it runs pactl / SwitchAudioSource.
manifest_path = csproj.parent / "manifest.json"
manifest = json.loads(manifest_path.read_text())
assert manifest.get("id") == plugin_id, manifest.get("id")
manifest["description"] = ("Lists your audio outputs and inputs on a 4x3 deck widget and switches the system "
                           "default with a tap, plus a 'Set audio devices' action. Linux (PipeWire/PulseAudio "
                           "via pactl), Windows (Core Audio) and macOS (SwitchAudioSource).")
perms = manifest.get("permissions", [])
if "process:spawn" not in perms:
    perms.append("process:spawn")
manifest["permissions"] = perms
if repo_url.startswith("https://"):
    manifest["repository"] = repo_url
manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
print("manifest updated")
PY

# Drop the test project from the solution (it tested the template's example action).
for sln in "$ROOT"/*.slnx; do
  [ -f "$sln" ] && sed -i '/[Tt]ests/d' "$sln"
done

step "Compiling"
( cd "$SRC" && dotnet build -c Release )

step "Packing"
mkdir -p "$WORK/artifacts"
( cd "$SRC" && macrodeck-plugin build --output "$WORK/artifacts" )
ARTIFACT="$(ls -t "$WORK"/artifacts/*.macroDeckPlugin | head -n1)"
macrodeck-plugin validate --artifact "$ARTIFACT" || true

printf '\n\033[1;32mBuilt: %s\033[0m\n' "$ARTIFACT"
echo "Install it: Macro Deck -> Integrations -> Add -> Install from file... (confirm the unsigned prompt)."

if [ "${1:-}" = "--run" ]; then
  step "Running against the open Macro Deck (approve the pairing prompt; Ctrl-C to stop)"
  cd "$SRC" && macrodeck-plugin run --project "$CSPROJ"
fi
