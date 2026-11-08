#!/usr/bin/env bash
#
# Publish the extension bundle that the Kahuna server loads with --extension-assembly, and zip it.
#
# The bundle is Kahuna.Storage.Gcs.dll, its .deps.json, and the Google client libraries it needs. It
# carries no copy of anything the server supplies (Kahuna.*, Kommander, Nixie, Grpc.*, Google.Protobuf,
# the RocksDB and SQLite natives): the server always loads those from itself, so a copy is only weight,
# and the script fails when one appears.
#
# Usage:
#   scripts/publish-bundle.sh                     # bundle into <repo>/artifacts/bundle, zip next to it
#   scripts/publish-bundle.sh -o /tmp/gcs-bundle  # bundle into another directory
#   scripts/publish-bundle.sh --no-zip            # only publish and check the bundle
#   scripts/publish-bundle.sh -c Debug            # publish a non-Release configuration
#
# The zip is <output>/../kahuna-storage-gcs-<version>.zip, with the version from the csproj.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/Kahuna.Storage.Gcs/Kahuna.Storage.Gcs.csproj"

CONFIGURATION="Release"
OUTPUT="$REPO_ROOT/artifacts/bundle"
ZIP=1

usage() {
  sed -n '2,16p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    -h|--help)          usage 0 ;;
    --no-zip)           ZIP=0; shift ;;
    -c|--configuration) CONFIGURATION="$2"; shift 2 ;;
    -o|--output)        OUTPUT="$2"; shift 2 ;;
    *)                  echo "error: unknown argument: $1" >&2; usage 1 ;;
  esac
done

VERSION="$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' "$PROJECT" | head -n 1)"
if [[ -z "$VERSION" ]]; then
  echo "error: no <Version> in $PROJECT" >&2
  exit 1
fi

mkdir -p "$OUTPUT"
OUTPUT="$(cd "$OUTPUT" && pwd)"

# A private work directory keeps the bundle restore away from the obj/ of the normal build. The two
# restores differ (the bundle excludes the Kahuna.Core runtime assets), and NuGet reads the package
# dependencies from the restore output, so sharing one would let a bundle publish change what a later
# pack declares.
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

rm -rf "${OUTPUT:?}"/*

dotnet publish "$PROJECT" \
  -c "$CONFIGURATION" \
  -p:ExtensionBundle=true \
  --artifacts-path "$WORK" \
  -o "$OUTPUT"

# Fail on any copy of what the server supplies.
host_copies="$(cd "$OUTPUT" && find . -type f \( \
    \( -name 'Kahuna.*' ! -name 'Kahuna.Storage.Gcs.*' \) \
    -o -name 'Kommander*' -o -name 'Nixie*' -o -name 'Grpc.*' -o -name 'Google.Protobuf*' \
    -o -name '*rocksdb*' -o -name '*e_sqlite3*' -o -name 'SQLitePCLRaw*' \) | sort)"

if [[ -n "$host_copies" ]]; then
  echo "error: the bundle carries copies of assemblies the server supplies:" >&2
  echo "$host_copies" >&2
  exit 1
fi

if [[ ! -f "$OUTPUT/Kahuna.Storage.Gcs.deps.json" ]]; then
  echo "error: the bundle has no Kahuna.Storage.Gcs.deps.json; the server cannot resolve its dependencies" >&2
  exit 1
fi

echo "bundle: $OUTPUT ($(du -sh "$OUTPUT" | cut -f1))"

if [[ "$ZIP" -eq 1 ]]; then
  ZIP_PATH="$(dirname "$OUTPUT")/kahuna-storage-gcs-$VERSION.zip"
  rm -f "$ZIP_PATH"
  (cd "$OUTPUT" && zip -q -r "$ZIP_PATH" .)
  echo "zip: $ZIP_PATH"
fi
