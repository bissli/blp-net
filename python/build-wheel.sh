#!/usr/bin/env bash
# Builds the blp wheel with Blp.dll and its dependencies in blp/lib, staged
# outside the Dropbox checkout. The one argument is the output directory,
# /tmp/blp-wheel by default.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
out=$(realpath -m "${1:-/tmp/blp-wheel}")
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT

cp -r "$repo/python/pyproject.toml" "$repo/python/src" "$stage"
dotnet publish "$repo/src/Blp" -c Release -o "$stage/src/blp/lib"
cd "$stage"
POETRY_VIRTUALENVS_CREATE=false poetry build --format wheel --output "$out"
