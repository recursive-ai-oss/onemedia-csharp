#!/usr/bin/env bash
# Worker-side sweep for the MPEG readers: builds the harness, then runs the packet differential, the track/duration check and the
# metadata comparison on the generated and published MPEG files, into $1 (default ~/onemedia/u13-mpeg/out).
#   tools/run-mpeg-sweep.sh [OUT] [build]
set -uo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
O="${1:-$HOME/onemedia/u13-mpeg/out}"; mkdir -p "$O"
C="${ONEMEDIA_HOME:-$HOME/onemedia}/corpus"
G="$C/generated/mpeg"; S="$C/samples"
bash "$here/run-mpeg-diff.sh" build 2>&1 | tail -3
TS=$(ls "$G"/ts/*.ts "$G"/m2ts/*.m2ts "$S"/mpeg/*.ts "$S"/avchd/* 2>/dev/null)
PS=$(ls "$G"/ps/* "$S"/mpeg/*.mpg "$S"/mpeg/*.mpeg "$S"/mpeg/*.VOB 2>/dev/null)
ES=$(ls "$G"/es/* "$S"/mpeg/*.264 "$S"/mpeg/*.h264 "$S"/mpeg/*.m2v 2>/dev/null)
ALL="$TS $PS $ES"
bash "$here/run-mpeg-diff.sh" run diff --jobs 4 --out "$O" --exceptions "$here/OneMedia.Oracles/exceptions.tsv" $ALL > "$O/diff.log" 2>&1
tail -3 "$O/diff.log"
bash "$here/run-mpeg-diff.sh" run infocheck $ALL > "$O/infocheck.log" 2>&1
tail -3 "$O/infocheck.log"
bash "$here/run-mpeg-diff.sh" run meta --out "$O" $(ls "$S"/avchd/* "$G"/ts/named-service.ts "$G"/ts/h264-aac.ts) > "$O/meta.log" 2>&1
tail -3 "$O/meta.log"
ls "$O"
