#!/usr/bin/env bash
# Rebuilds the MatOS container and redeploys the dev stack (the project's live-reload / test workflow).
#   ./scripts/redeploy.sh [--fresh] [--port 8030]
set -euo pipefail

fresh=0
port=8030
while [[ $# -gt 0 ]]; do
  case "$1" in
    --fresh) fresh=1 ;;
    --port) port="$2"; shift ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

cd "$(dirname "$0")/.."
export MATOS_CHANNEL=local
export MATOS_BUILD_DATE="$(date -u +%Y%m%d)"
export MATOS_PORT="$port"

# The dev stack was "mattiverse-dev" until the rename: its containers hold the ports and its network the subnet. They go once; the volume stays
# (docker-compose.dev.yml uses it).
if docker container inspect mattiverse-dev >/dev/null 2>&1; then
  echo "Removing the old dev stack of Mattiverse (its volume stays) ..."
  docker stop mattiverse-dev mattiverse-dev-sqlite-web || true
  docker rm mattiverse-dev mattiverse-dev-sqlite-web || true
  docker network rm mattiverse-dev_default || true
fi

if [[ $fresh -eq 1 ]]; then
  echo "Removing the stack and its volume ..."
  docker compose -f docker-compose.dev.yml down -v
fi

docker compose -f docker-compose.dev.yml up -d --build --remove-orphans

echo
echo "MatOS local-${MATOS_BUILD_DATE} is running on http://localhost:${port}"
echo "Dev login: admin@example.com / Start12345   |   SQLite web: http://localhost:8021"
