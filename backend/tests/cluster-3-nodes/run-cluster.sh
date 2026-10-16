#!/usr/bin/env bash
# Prépare le cluster depuis un snapshot frais de origin/main. Le poste de travail
# courant n'est ni réinitialisé ni modifié.
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(git -C "$script_dir" rev-parse --show-toplevel)"
git -C "$repo_root" fetch --quiet origin main
main_sha="$(git -C "$repo_root" rev-parse origin/main)"
short_sha="${main_sha:0:12}"
run_root="/tmp/encoding-cluster-main-${short_sha}"
source_dir="$run_root/source"
media_dir="$run_root/media/lib"
project="encoding-cluster-${short_sha}"
image="encoding:main-${short_sha}"
env_file="$run_root/compose.env"
compose_file="$source_dir/backend/tests/cluster-3-nodes/compose.yml"
compose=(docker --host=unix:///var/run/docker.sock compose --project-name "$project" --env-file "$env_file" -f "$compose_file")
started=0

mkdir -p "$run_root"
if [[ ! -f "$source_dir/.main-sha" ]] || [[ "$(<"$source_dir/.main-sha")" != "$main_sha" ]]; then
  stage="$(mktemp -d "$run_root/source.tmp.XXXXXX")"
  git -C "$repo_root" archive "$main_sha" | tar -x -C "$stage"
  printf '%s\n' "$main_sha" > "$stage/.main-sha"
  if [[ -e "$source_dir" ]]; then
    mv "$source_dir" "$run_root/source.previous.$(date +%s)"
  fi
  mv "$stage" "$source_dir"
fi
mkdir -p "$media_dir"

docker_ready=0
for ((attempt = 0; attempt < 30; attempt++)); do
  if env -u DOCKER_HOST -u DOCKER_CONTEXT -u DOCKER_TLS -u DOCKER_TLS_VERIFY -u DOCKER_CERT_PATH \
    docker --host=unix:///var/run/docker.sock info >/dev/null 2>&1; then
    docker_ready=1
    break
  fi
  sleep 2
done
if (( ! docker_ready )); then
  echo "Le démon Docker n'est pas prêt sur le socket local /var/run/docker.sock." >&2
  exit 1
fi
export DOCKER_HOST=unix:///var/run/docker.sock

if ! docker --host=unix:///var/run/docker.sock image inspect "$image" >/dev/null 2>&1; then
  build_args=(docker --host=unix:///var/run/docker.sock build --progress=plain -f "$source_dir/backend/Dockerfile" -t "$image")
  if [[ -n "${CODEX_PROXY_CERT:-}" && -r "$CODEX_PROXY_CERT" ]]; then
    build_args+=(--secret "id=proxy_ca,src=$CODEX_PROXY_CERT")
  fi
  build_args+=("$source_dir")
  "${build_args[@]}"
fi

if [[ ! -f "$env_file" ]]; then
  umask 077
  printf 'ENCODING_IMAGE=%s\nMEDIA_DIR=%s\nSETUP_TOKEN=%s\nPG_PASSWORD=%s\n' \
    "$image" "$media_dir" "$(openssl rand -hex 16)" "$(openssl rand -hex 24)" > "$env_file"
  chmod 600 "$env_file"
fi

cleanup_failed_startup() {
  status=$?
  if (( status != 0 && started )); then
    "${compose[@]}" logs --no-color > "$run_root/startup-failure.log" 2>&1 || true
    "${compose[@]}" down -v --remove-orphans || true
    echo "Initialisation interrompue ; les journaux sont dans $run_root/startup-failure.log" >&2
  fi
  exit "$status"
}
trap cleanup_failed_startup EXIT

if [[ ! -f "$media_dir/.sources.sha256" ]]; then
  docker run --rm --network none --entrypoint /bin/bash \
    -v "$source_dir/backend/tests/cluster-3-nodes:/test:ro" \
    -v "$media_dir:/media" "$image" /test/gen-media.sh /media
fi
started=1
"${compose[@]}" up -d --wait --wait-timeout 180
export COMPOSE_PROJECT_NAME="$project"
set -a
source "$env_file"
set +a
cluster_no_proxy="localhost,127.0.0.1,node1,node2,node3,postgres"
for service in node1 node2 node3 postgres; do
  address="$(docker inspect -f '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' "$project-$service-1")"
  cluster_no_proxy+=",$address"
done
export NO_PROXY="${NO_PROXY:+$NO_PROXY,}$cluster_no_proxy"
export no_proxy="${no_proxy:+$no_proxy,}$cluster_no_proxy"
printf 'export DOCKER_HOST=%q\nexport COMPOSE_PROJECT_NAME=%q\nexport ENCODING_IMAGE=%q\nexport MEDIA_DIR=%q\n' \
  "$DOCKER_HOST" "$project" "$image" "$media_dir" > "$run_root/test-env.sh"
printf 'export NO_PROXY="${NO_PROXY:+$NO_PROXY,}%s"\nexport no_proxy="${no_proxy:+$no_proxy,}%s"\n' \
  "$cluster_no_proxy" "$cluster_no_proxy" >> "$run_root/test-env.sh"
cd "$source_dir/backend/tests/cluster-3-nodes"
python3 "$source_dir/backend/tests/cluster-3-nodes/bootstrap.py"
python3 "$source_dir/backend/tests/cluster-3-nodes/check-cluster.py"

trap - EXIT
printf '\nCluster prêt depuis origin/main@%s.\n' "$short_sha"
printf 'Sources : %s\n' "$source_dir"
printf 'Environnement des commandes de test : source %s\n' "$run_root/test-env.sh"
printf 'Compose : docker compose --project-name %s --env-file %s -f %s\n' "$project" "$env_file" "$compose_file"
printf 'Nettoyage : docker compose --project-name %s --env-file %s -f %s down -v\n' "$project" "$env_file" "$compose_file"
