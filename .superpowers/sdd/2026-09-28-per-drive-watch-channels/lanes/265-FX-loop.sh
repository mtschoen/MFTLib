#!/bin/bash
# usage: loop.sh N ; prints failure count
f=0
for i in $(seq 1 $1); do
  dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64 --no-build --filter "FullyQualifiedName~OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock" 2>&1 | grep -q "Failed!" && f=$((f+1))
done
echo "failures $f of $1"
