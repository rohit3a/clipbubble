#!/bin/zsh
# Runs the Swift tests, then the Linux suite, then the Windows sync suite when dotnet is installed.
set -e
cd "$(dirname "$0")"
mkdir -p .build
swiftc -swift-version 5 $(ls Sources/*.swift | grep -v main.swift) Tests/*.swift -o .build/tests
.build/tests
if [[ -d Linux/tests ]]; then
  (cd Linux && python3 -m unittest discover -s tests -t . -q)
fi
if command -v dotnet >/dev/null; then
  (cd Windows/SyncTests && dotnet build -c Release -nologo -v q && dotnet bin/Release/net10.0/ClipBubble.SyncTests.dll)
fi
