#!/usr/bin/env bash
# Builds the Windows release zip: artifacts/GSBC.WirecastNDI-win-x64.zip
# (Self-contained, so the Wirecast PC doesn't need .NET installed.)
set -euo pipefail
cd "$(dirname "$0")"

out=artifacts/win-x64
rm -rf "$out" artifacts/GSBC.WirecastNDI-win-x64.zip

dotnet publish GSBC.WirecastNDI/GSBC.WirecastNDI.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None \
  -o "$out"

cp deploy/install.ps1 deploy/uninstall.ps1 deploy/Install.cmd README.md "$out/"

(cd "$out" && zip -qr ../GSBC.WirecastNDI-win-x64.zip .)
echo "Built artifacts/GSBC.WirecastNDI-win-x64.zip"
ls -la "$out"
