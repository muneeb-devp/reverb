#!/usr/bin/env bash
# Checks the promise at the top of the README: a notebook is an ordinary .cs file that runs with
# plain `dotnet run`. Packs Reverb.Runtime from this checkout into a throwaway feed, copies a
# sample notebook next to it and runs it with nothing but the .NET SDK.
#
#   eng/check-dotnet-run.sh
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
# Git Bash on Windows hands out /tmp/... paths that only Bash understands. dotnet needs C:/... ones.
if command -v cygpath >/dev/null 2>&1; then work="$(cygpath -m "$work")"; fi
trap 'rm -rf "$work"' EXIT

# A clean package cache, so nothing left over from earlier builds can stand in for the package.
export NUGET_PACKAGES="$work/packages"
export DOTNET_NOLOGO=true
export DOTNET_CLI_TELEMETRY_OPTOUT=true

version="0.0.0-check"
dotnet pack "$repo/src/Reverb.Runtime" -c Release -o "$work/feed" -p:Version="$version" --nologo -v q

mkdir "$work/notebook"
cp -R "$repo/samples/sales-explorer.cs" "$repo/samples/data" "$work/notebook/"
cd "$work/notebook"
sed "s|#:package Reverb.Runtime@.*|#:package Reverb.Runtime@$version|" sales-explorer.cs > sales.tmp && mv sales.tmp sales-explorer.cs

cat > nuget.config <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$work/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
# Keeps the repo's build settings out of the notebook.
echo "<Project />" > Directory.Build.props
echo "<Project />" > Directory.Build.targets

out="$(dotnet run sales-explorer.cs 2>&1)" || { echo "$out"; echo "FAIL: dotnet run exited with an error"; exit 1; }
echo "$out" | grep -v NU1903 || true
echo "$out" | tr -d '\r' | grep -qE 'Revenue|East' || { echo "FAIL: the notebook ran but printed nothing from its cells"; exit 1; }
echo "OK: sales-explorer.cs ran with plain dotnet run"
