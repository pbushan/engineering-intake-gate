#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repository_root"

COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME:-engineeringintakegate_product_startup_$$}
export COMPOSE_PROJECT_NAME
auth_cookie_jar=$(mktemp "${TMPDIR:-/tmp}/intake-gate-product-auth.XXXXXX")

compose() {
    docker compose "$@"
}

cleanup() {
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
    rm -f "$auth_cookie_jar"
}
trap cleanup EXIT INT TERM

service_count=$(compose config --services | wc -l | tr -d ' ')
[ "$service_count" = "2" ] || { echo "Product Compose must contain the backend and one stateless UI service." >&2; exit 1; }

compose up --build --detach

attempts=0
while [ "$attempts" -lt 60 ]; do
    if health=$(curl --fail --silent --show-error http://127.0.0.1:8080/health/ready 2>/dev/null); then
        break
    fi
    attempts=$((attempts + 1))
    sleep 1
done
[ "$attempts" -lt 60 ] || { compose logs >&2; exit 1; }

echo "$health" | grep '"status":"ready"' >/dev/null
echo "$health" | grep '"setupStatus":"incomplete"' >/dev/null
curl --fail --silent --show-error http://127.0.0.1:8080/ | grep '<title>Engineering Intake Gate</title>' >/dev/null
curl --fail --silent --show-error http://127.0.0.1:8080/a/deep/spa/route | grep '<title>Engineering Intake Gate</title>' >/dev/null
root_headers=$(curl --fail --silent --show-error --head http://127.0.0.1:8080/ | tr -d '\r')
echo "$root_headers" | grep -i '^Content-Security-Policy:' >/dev/null
echo "$root_headers" | grep -i '^Permissions-Policy:' >/dev/null
echo "$root_headers" | grep -i '^Cross-Origin-Opener-Policy: same-origin$' >/dev/null
echo "$root_headers" | grep -i '^X-Content-Type-Options: nosniff$' >/dev/null
asset_path=$(curl --fail --silent --show-error http://127.0.0.1:8080/ | sed -n 's/.*src="\([^\"]*\.js\)".*/\1/p')
[ -n "$asset_path" ] || { echo "Product UI did not publish a JavaScript entry asset." >&2; exit 1; }
asset_headers=$(curl --fail --silent --show-error --head "http://127.0.0.1:8080$asset_path" | tr -d '\r')
echo "$asset_headers" | grep -i '^Content-Security-Policy:' >/dev/null
echo "$asset_headers" | grep -i '^Cache-Control: public, immutable$' >/dev/null
profile_status=$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:8080/api/profile)
[ "$profile_status" = "401" ] || { echo "Fresh Product Compose exposed profile data anonymously." >&2; exit 1; }

csrf_json=$(curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
    http://127.0.0.1:8080/api/auth/csrf)
csrf_token=$(echo "$csrf_json" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
    --header "X-CSRF-TOKEN: $csrf_token" --header 'Content-Type: application/json' \
    --data '{"username":"product-admin","password":"deterministic-product-admin-password"}' \
    http://127.0.0.1:8080/api/auth/bootstrap >/dev/null
profile_status=$(curl --silent --output /dev/null --write-out '%{http_code}' --cookie "$auth_cookie_jar" \
    http://127.0.0.1:8080/api/profile)
[ "$profile_status" = "200" ] || { echo "Authenticated Product Compose could not read profileless state." >&2; exit 1; }
curl --fail --silent --show-error --cookie "$auth_cookie_jar" \
    http://127.0.0.1:8080/api/profile | grep '"exists":false' >/dev/null
curl --fail --silent --show-error --cookie "$auth_cookie_jar" \
    http://127.0.0.1:8080/api/version | grep '"version":"2026.9.3"' >/dev/null

missing_csrf_status=$(curl --silent --output /dev/null --write-out '%{http_code}' \
    --cookie "$auth_cookie_jar" --header 'Content-Type: application/json' --data '{}' \
    http://127.0.0.1:8080/api/auth/logout)
[ "$missing_csrf_status" = "400" ] || { echo "Same-origin proxy did not preserve backend CSRF enforcement." >&2; exit 1; }

# AUTH-009 / DEP-003: a backend container replacement must retain the backend-owned
# session because its standard ASP.NET Data Protection key ring shares the durable
# application volume (and remains separate from credential encryption material).
backend_container_before=$(compose ps --quiet intake-gate)
compose up --detach --no-deps --force-recreate intake-gate >/dev/null
attempts=0
while [ "$attempts" -lt 30 ]; do
    if curl --fail --silent --show-error http://127.0.0.1:8080/health/ready >/dev/null 2>&1; then
        break
    fi
    attempts=$((attempts + 1))
    sleep 1
done
[ "$attempts" -lt 30 ] || { compose logs >&2; exit 1; }
backend_container_after=$(compose ps --quiet intake-gate)
[ "$backend_container_before" != "$backend_container_after" ] || { echo "Backend recreation did not replace the container." >&2; exit 1; }
session_status=$(curl --silent --output /dev/null --write-out '%{http_code}' --cookie "$auth_cookie_jar" \
    http://127.0.0.1:8080/api/auth/session)
[ "$session_status" = "200" ] || { echo "Backend recreation invalidated the durable authenticated session." >&2; exit 1; }
compose exec -T intake-gate sh -c \
    'test -d /app/data/data-protection-keys && test "$(stat -c %a /app/data/data-protection-keys)" = 700 && test "$(find /app/data/data-protection-keys -type f | wc -l)" -gt 0'

backend_container_before=$(compose ps --quiet intake-gate)
compose restart intake-gate-ui >/dev/null
attempts=0
while [ "$attempts" -lt 30 ]; do
    if curl --fail --silent --show-error http://127.0.0.1:8080/ui-health >/dev/null 2>&1; then
        break
    fi
    attempts=$((attempts + 1))
    sleep 1
done
[ "$attempts" -lt 30 ] || { compose logs >&2; exit 1; }
backend_container_after=$(compose ps --quiet intake-gate)
[ "$backend_container_before" = "$backend_container_after" ] || { echo "UI restart unexpectedly replaced the backend container." >&2; exit 1; }
session_status=$(curl --silent --output /dev/null --write-out '%{http_code}' --cookie "$auth_cookie_jar" \
    http://127.0.0.1:8080/api/auth/session)
[ "$session_status" = "200" ] || { echo "UI restart lost the backend-owned authenticated session." >&2; exit 1; }
curl --fail --silent --show-error --cookie "$auth_cookie_jar" \
    http://127.0.0.1:8080/api/profile | grep '"exists":false' >/dev/null

echo "Product startup passed: backend plus stateless UI, same-origin SPA/API routing, cookies, CSRF, profileless bootstrap, durable session across backend recreation, and UI-only restart persistence."
