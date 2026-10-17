#!/bin/zsh
# Runs the Swift tests, then the Linux suite when its folder exists.
set -e
cd "$(dirname "$0")"
mkdir -p .build
swiftc -swift-version 5 $(ls Sources/*.swift | grep -v main.swift) Tests/*.swift -o .build/tests
.build/tests
if [[ -d Linux/tests ]]; then
  (cd Linux && python3 -m unittest discover -s tests -t . -q)
fi
