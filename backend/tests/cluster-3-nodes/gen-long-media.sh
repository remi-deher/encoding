#!/usr/bin/env bash
# 6 clips synthétiques de 60 s (854x480) pour les tests de panne (tâches assez longues pour être interrompues). Usage : gen-long-media.sh <dossier>
set -euo pipefail; dir="${1:?dossier}"
for i in 1 2 3 4 5 6; do
  ffmpeg -nostdin -v error -y -f lavfi -i "testsrc2=size=854x480:rate=25" -f lavfi -i "sine=frequency=$((200+i*50)):sample_rate=44100" \
    -t 60 -c:v libx264 -preset ultrafast -crf 30 -pix_fmt yuv420p -c:a aac -b:a 64k -shortest "$dir/long$i.mkv"
done
(cd "$dir" && sha256sum long*.mkv >> .sources.sha256); ls -l "$dir"/long*.mkv | awk '{print $5, $9}'
