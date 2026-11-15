#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 4 ]]; then
    echo 'Usage: Publish-Docker.sh SOURCE CACHE OUTPUT REVISION' >&2
    exit 2
fi

source_root=$(cd "$1" && pwd -P)
mkdir -p "$2" "$3"
cache_root=$(cd "$2" && pwd -P)
publish_root=$(cd "$3" && pwd -P)
revision=$4

case "$cache_root/" in
    "$source_root/"*)
        echo 'The compilation cache must be outside the source directory.' >&2
        exit 2
        ;;
esac
case "$publish_root/" in
    "$cache_root/"*)
        echo 'Published output must be outside the compilation cache.' >&2
        exit 2
        ;;
esac

cached_source="$cache_root/source"
if [[ ! -d "$cached_source" ]]; then
    mkdir -p "$cached_source"
    cp -R -- "$source_root/." "$cached_source/"
    echo 'Initialized Eden compilation source cache.'
fi
updated=0
removed=0

# MSBuild uses input timestamps. Keep unchanged inputs stable even after a fresh checkout,
# but give changed contents a new timestamp regardless of their original modification time.
# Hash in batches instead of spawning one comparison process for every source file.
find "$cached_source" -type f -exec sha256sum --zero -- {} + > "$cache_root/cached-inputs.sha256"
find "$source_root" -type f -exec sha256sum --zero -- {} + > "$cache_root/current-inputs.sha256"
declare -A cached_hashes
# GNU sha256sum --zero emits 64 hex digits, two mode/separator bytes, then the path and NUL.
readonly digest_length=64
readonly path_offset=$((digest_length + 2))
while IFS= read -r -d '' entry; do
    input=${entry:path_offset}
    relative=${input#"$cached_source/"}
    cached_hashes["$relative"]=${entry:0:digest_length}
done < "$cache_root/cached-inputs.sha256"

while IFS= read -r -d '' entry; do
    input=${entry:path_offset}
    relative=${input#"$source_root/"}
    destination="$cached_source/$relative"
    if [[ ${cached_hashes["$relative"]-} != "${entry:0:digest_length}" ]]; then
        mkdir -p "$(dirname "$destination")"
        cp --remove-destination -- "$input" "$destination"
        updated=$((updated + 1))
    fi
done < "$cache_root/current-inputs.sha256"

for relative in "${!cached_hashes[@]}"; do
    if [[ ! -f "$source_root/$relative" ]]; then
        rm -- "$cached_source/$relative"
        removed=$((removed + 1))
    fi
done

printf 'Eden compilation cache: %s updated inputs, %s removed inputs.\n' "$updated" "$removed"
cd "$cached_source"
# Restore inside this invocation: BuildKit may evict this cache independently of image layers.
publish_started=$(date +%s%N)
dotnet publish src/Eden.Server/Eden.Server.csproj -c Release --disable-build-servers \
    --artifacts-path "$cache_root/artifacts" -o "$publish_root" \
    -p:UseAppHost=false "-p:EdenBuildRevision=$revision"
# A published administration page needs its framework script even when compilation was reused.
test -s "$publish_root/wwwroot/_framework/blazor.web.js"
publish_elapsed=$((($(date +%s%N) - publish_started) / 1000000))
printf 'Eden dotnet publish: %s ms.\n' "$publish_elapsed"
