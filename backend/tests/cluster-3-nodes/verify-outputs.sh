#!/usr/bin/env bash
# Vérifie sources (empreintes) et sorties (ffprobe + décodage complet) de la bibliothèque de test. Usage : verify-outputs.sh <MEDIA_DIR>
set -u; dir="${1:?MEDIA_DIR}"; fail=0
echo "== Intégrité des sources"; (cd "$dir" && sha256sum --quiet -c .sources.sha256 && echo "sources inchangées ($(wc -l < .sources.sha256) fichiers)") || { echo "SOURCES MODIFIÉES"; fail=1; }
echo "== Sorties"
while IFS='|' read -r media exec_id; do
  f=$(ls "$dir"/.encoding-results/$exec_id/result.* 2>/dev/null | head -1)
  [ -n "$f" ] || { echo "$media: AUCUNE SORTIE ($exec_id)"; fail=1; continue; }
  src="$dir/$media"
  vc=$(ffprobe -v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 "$f"); ac=$(ffprobe -v error -select_streams a:0 -show_entries stream=codec_name -of csv=p=0 "$f" </dev/null)
  ds=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$src"); do_=$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$f" </dev/null)
  dec=$(ffmpeg -nostdin -v error -i "$f" -f null - </dev/null 2>&1 | wc -l)
  ok=$(python3 -c "print('OK' if '$vc'=='h264' and '$ac' and abs(float('$ds')-float('$do_'))<0.15 and $dec==0 else 'KO')")
  [ "$ok" = OK ] || fail=1
  echo "$media: $ok video=$vc audio=$ac dur_src=$ds dur_out=$do_ erreurs_decodage=$dec taille=$(stat -c%s "$f")o ($exec_id)"
done < <(docker exec -i encoding-cluster-test-postgres-1 psql -U encoding -d encoding -At -F'|' -c "SELECT e.media, e.id FROM executions e WHERE e.state='succeeded' ORDER BY e.media, e.queued_at")
exit $fail
