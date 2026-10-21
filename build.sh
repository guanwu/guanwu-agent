#!/usr/bin/env bash
# Copyright (C) 2026 Jiaowei <wei.jiao@guanwu.com>
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RID=""
SKIP_TESTS=0

for argument in "$@"; do
  case "$argument" in
    --skip-tests)
      SKIP_TESTS=1
      ;;
    -*)
      echo "error: unknown option: $argument" >&2
      exit 2
      ;;
    *)
      if [ -n "$RID" ]; then
        echo "error: unexpected argument: $argument" >&2
        exit 2
      fi
      RID="$argument"
      ;;
  esac
done

if [ -z "$RID" ]; then
  case "$(uname -s)" in
    Linux) os=linux ;;
    Darwin) os=osx ;;
    *)
      echo "error: unsupported operating system: $(uname -s)" >&2
      exit 1
      ;;
  esac

  case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    arm64 | aarch64) arch=arm64 ;;
    *)
      echo "error: unsupported architecture: $(uname -m)" >&2
      exit 1
      ;;
  esac

  RID="$os-$arch"
fi

echo "Building Guanwu Agent for $RID"

for tool in dotnet node npm; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "error: $tool was not found on PATH" >&2
    exit 1
  fi
done

# Native AOT needs clang on Linux and the Xcode command line tools on macOS.
case "$RID" in
  linux-*)
    if ! command -v clang >/dev/null 2>&1; then
      echo "error: clang is required for Native AOT; install clang and zlib1g-dev" >&2
      exit 1
    fi
    ;;
  osx-*)
    if ! xcode-select -p >/dev/null 2>&1; then
      echo "error: Xcode command line tools are required for Native AOT" >&2
      exit 1
    fi
    ;;
esac

cd "$ROOT"

if [ -f package-lock.json ]; then
  npm ci
else
  npm install
fi
npm run typecheck
npm run build

OUT="$ROOT/release/$RID"
rm -rf -- "$OUT"
dotnet publish src/GuanwuAgent/GuanwuAgent.csproj -c Release -r "$RID" -o "$OUT"

rm -rf -- "$OUT/docs"
cp -R docs "$OUT/docs"
cp README.md "$OUT/README.md"
cp LICENSE "$OUT/LICENSE"

if [ "$SKIP_TESTS" -eq 0 ]; then
  node tests/smoke.mjs "$OUT/guanwu-agent"
fi

printf '\nBuilt %s\n' "$OUT"
