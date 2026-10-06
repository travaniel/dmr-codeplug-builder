#!/usr/bin/env bash
# Builds CodeplugBuilder.exe (and runs the engine tests) on Linux.
# Needs: .NET 8 SDK and Mono's .NET Framework 4.8 reference assemblies
#   (Ubuntu: apt install dotnet-sdk-8.0 mono-devel libmono-system-windows-forms4.0-cil)
# On Windows, just open CodeplugBuilder.sln in Visual Studio 2022 or run `dotnet build -c Release`.
set -euo pipefail
cd "$(dirname "$0")"
OFFLINE=tools/nuget.offline.config   # the projects have no NuGet dependencies

dotnet restore src/App/CodeplugBuilder.csproj --configfile "$OFFLINE"
dotnet build   src/App/CodeplugBuilder.csproj -c Release --no-restore -nologo

dotnet restore tests/CodeplugBuilder.Tests.csproj --configfile "$OFFLINE"
dotnet build   tests/CodeplugBuilder.Tests.csproj -c Release --no-restore -nologo
# Pass a CPS "Export All" folder to also run the round-trip tests against a real codeplug.
dotnet tests/bin/Release/net8.0/CodeplugBuilder.Tests.dll "${1:-}"

echo
echo "Built: src/App/bin/Release/net48/CodeplugBuilder.exe"
