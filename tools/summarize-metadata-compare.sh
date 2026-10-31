#!/usr/bin/env bash
# Summarizes metadata-compare.tsv: counts per kind and verdict, then every row that is not a match.
# Usage: tools/summarize-metadata-compare.sh <metadata-compare.tsv>
set -eu
T=$1
echo "== counts per kind and verdict"
awk -F'\t' 'NR>1{c[$2" "$7]++} END{for(k in c) print c[k]"\t"k}' "$T" | sort -k2
echo "== rows to explain"
awk -F'\t' 'NR>1 && ($7=="differs"||$7=="ours-missing"||$7=="unparsed"||$7=="unreported"){print substr($1,1,26)"|"$2"|"$3"|"substr($4,1,40)"|"substr($5,1,40)"|"substr($6,1,40)"|"$7}' "$T" | sort | uniq -c
echo "== per kind and field: rows; ExifTool agree/reported; MediaInfo agree/reported (native rows pooled)"
awk -F'\t' 'NR>1{k=$2" "$3; if($2=="native"){k="native"} if($8!="none"){er[k]++; if($8=="agree")ea[k]++} if($9!="none"){mr[k]++; if($9=="agree")ma[k]++} n[k]++} END{for(k in n) printf "%s\trows %d\tET %d/%d\tMI %d/%d\n", k, n[k], ea[k], er[k], ma[k], mr[k]}' "$T" | sort
