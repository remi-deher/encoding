#!/usr/bin/env bash
# Installe des paquets apt sur un runner GitHub sans rester bloqué sur un miroir lent :
# délais de connexion courts, nouvelles tentatives par apt, puis jusqu'à 3 essais bornés à 4 minutes chacun.
set -euo pipefail
options=(-o Acquire::Retries=3 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 -o Dpkg::Use-Pty=0)
for attempt in 1 2 3; do
  if sudo timeout 240 apt-get "${options[@]}" update && sudo timeout 240 apt-get "${options[@]}" install -y --no-install-recommends "$@"; then
    exit 0
  fi
  echo "apt a échoué ou dépassé 4 minutes (essai $attempt/3)." >&2
  sleep 5
done
exit 1
