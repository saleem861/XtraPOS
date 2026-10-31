#!/usr/bin/env bash
# Restore XtraPOS dependencies for a Cloud Agent. Toolchains are already on the
# image: Node.js 22 in /usr/local and the .NET 8 SDK in /usr/share/dotnet.
set -euo pipefail

export DOTNET_ROOT=/usr/share/dotnet
export PATH="/usr/local/bin:${DOTNET_ROOT}:${PATH}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export NUGET_XMLDOC_MODE=skip

root="$(pwd)"
if [[ ! -f "${root}/XtraPOS.sln" ]]; then
  echo "Run this script from the XtraPOS repository root." >&2
  exit 1
fi

npm ci --prefix src/XtraPOS.Web
dotnet restore XtraPOS.sln
