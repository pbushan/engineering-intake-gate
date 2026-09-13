#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repository_root"

echo "WARNING: removing Engineering Intake Gate containers and persistent development data volume."
docker compose -f test-harness/compose/docker-compose.test.yml down --volumes --remove-orphans
