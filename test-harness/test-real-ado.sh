#!/bin/sh
set -eu

: "${ADO_ORGANIZATION_URL:?Set ADO_ORGANIZATION_URL to a non-production/test Azure DevOps organization URL}"
: "${ADO_PROJECT:?Set ADO_PROJECT}"
: "${ADO_SAVED_QUERY_ID:?Set ADO_SAVED_QUERY_ID}"
: "${ADO_PAT:?Set ADO_PAT with Work Items read-only scope}"
: "${ADO_WORK_ITEM_ID:?Set ADO_WORK_ITEM_ID to an item governed by the saved query}"

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
database_path=$(mktemp "${TMPDIR:-/tmp}/intake-gate-real-ado.XXXXXX.db")
log_path=$(mktemp "${TMPDIR:-/tmp}/intake-gate-real-ado.XXXXXX.log")
auth_cookie_jar=$(mktemp "${TMPDIR:-/tmp}/intake-gate-real-ado-auth.XXXXXX")
port=${ADO_SMOKE_PORT:-8090}
pid=""

cleanup() {
    if [ -n "$pid" ]; then kill "$pid" >/dev/null 2>&1 || true; fi
    rm -f "$database_path" "$database_path-shm" "$database_path-wal" "$log_path" "$auth_cookie_jar"
}
trap cleanup EXIT INT TERM

cd "$repository_root"
echo "Explicitly importing the read-only smoke profile"
OperationalDatabase__Path="$database_path" \
dotnet run --project src/IntakeGate.Host/IntakeGate.Host.csproj --no-launch-profile -- \
    --import-legacy-profile "$repository_root/profiles/example/profile.yaml"

echo "Starting explicit read-only Azure DevOps smoke host"
ASPNETCORE_ENVIRONMENT=Development \
ASPNETCORE_URLS="http://127.0.0.1:$port" \
OperationalDatabase__Path="$database_path" \
AdoRuntime__OrganizationUrl="$ADO_ORGANIZATION_URL" \
AdoRuntime__Project="$ADO_PROJECT" \
AdoRuntime__SavedQueryId="$ADO_SAVED_QUERY_ID" \
AdoRuntime__CredentialEnvironmentVariable=INTAKE_REAL_ADO_PAT \
AdoRuntime__ReadAttachments="${ADO_SMOKE_READ_ATTACHMENTS:-false}" \
INTAKE_REAL_ADO_PAT="$ADO_PAT" \
AiRuntime__FakeScenario=pass \
dotnet run --project src/IntakeGate.Host/IntakeGate.Host.csproj --no-launch-profile >"$log_path" 2>&1 &
pid=$!

attempt=0
until curl --fail --silent "http://127.0.0.1:$port/health/ready" >/dev/null 2>&1; do
    attempt=$((attempt + 1))
    [ "$attempt" -lt 60 ] || { echo "Smoke host did not become ready." >&2; exit 1; }
    sleep 1
done

csrf_json=$(curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
    "http://127.0.0.1:$port/api/auth/csrf")
csrf_token=$(echo "$csrf_json" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
    --header "X-CSRF-TOKEN: $csrf_token" --header 'Content-Type: application/json' \
    --data '{"username":"smoke-admin","password":"deterministic-smoke-admin-password"}' \
    "http://127.0.0.1:$port/api/auth/bootstrap" >/dev/null
csrf_json=$(curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
    "http://127.0.0.1:$port/api/auth/csrf")
csrf_token=$(echo "$csrf_json" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
result=$(curl --fail --silent --show-error --cookie "$auth_cookie_jar" --header "X-CSRF-TOKEN: $csrf_token" \
    --request POST "http://127.0.0.1:$port/api/runs/work-items/$ADO_WORK_ITEM_ID")
echo "$result" | grep '"executionMode":"dryRun"' >/dev/null
echo "$result" | grep '"eligibility":"eligible"' >/dev/null
echo "$result" | grep "$ADO_PAT" >/dev/null && { echo "PAT appeared in endpoint response." >&2; exit 1; }
grep "$ADO_PAT" "$log_path" >/dev/null && { echo "PAT appeared in application logs." >&2; exit 1; }

echo "$result"
echo "Read-only ADO smoke passed. Attachment reads enabled: ${ADO_SMOKE_READ_ATTACHMENTS:-false}."
