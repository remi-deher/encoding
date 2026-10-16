#!/usr/bin/env bash
# Médias synthétiques (2 à 5 s, 320x240) + un fichier volontairement corrompu. Usage : gen-media.sh <dossier>
set -euo pipefail
dir="${1:?dossier requis}"; mkdir -p "$dir"
i=0
for spec in "3:mkv:testsrc" "2:mkv:smptebars" "4:mp4:testsrc2" "3:mp4:rgbtestsrc" "5:mkv:testsrc" "2:mp4:smptebars" "3:mkv:testsrc2" "4:mp4:testsrc"; do
  IFS=: read -r d ext src <<<"$spec"; i=$((i+1))
  ffmpeg -v error -y -f lavfi -i "$src=size=320x240:rate=25" -f lavfi -i "sine=frequency=$((300+i*80)):sample_rate=44100" \
    -t "$d" -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -b:a 64k -shortest "$dir/clip$i.$ext"
done
# fichier corrompu : en-tête valide tronqué puis octets aléatoires
head -c 4096 "$dir/clip1.mp4" > "$dir/corrupt.mp4" 2>/dev/null || head -c 4096 "$dir/clip3.mp4" > "$dir/corrupt.mp4"
head -c 2048 /dev/urandom >> "$dir/corrupt.mp4"
(cd "$dir" && sha256sum * > ../sources.sha256 && mv ../sources.sha256 ./.sources.sha256)
ls -l "$dir"
