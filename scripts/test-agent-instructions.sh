#!/usr/bin/env bash
# Shared with the docs job. No native build is needed for instruction tests.
set -euo pipefail
dotnet restore MFTLib.Tests/MFTLib.Tests.csproj
dotnet build MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-restore
dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter 'FullyQualifiedName~AgentInstructionsTests'
