#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repository_root"

# Preserve the named data volume during normal shutdown.
docker compose -f test-harness/compose/docker-compose.test.yml down --remove-orphans
