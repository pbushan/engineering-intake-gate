#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repository_root"

: "${REAL_AI_PROVIDER:?Set REAL_AI_PROVIDER to openai or anthropic}"
: "${REAL_AI_MODEL:?Set REAL_AI_MODEL to the provider model ID}"

case "$REAL_AI_PROVIDER" in
  openai) : "${OPENAI_API_KEY:?Set OPENAI_API_KEY}"; REAL_AI_API_KEY=$OPENAI_API_KEY ;;
  anthropic) : "${ANTHROPIC_API_KEY:?Set ANTHROPIC_API_KEY}"; REAL_AI_API_KEY=$ANTHROPIC_API_KEY ;;
  *) echo "REAL_AI_PROVIDER must be openai or anthropic." >&2; exit 2 ;;
esac
export REAL_AI_API_KEY
COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME:-engineeringintakegate_real_ai_$$}
export COMPOSE_PROJECT_NAME

compose_files="-f test-harness/compose/docker-compose.test.yml -f test-harness/compose/docker-compose.real-ai.yml"
cleanup() {
  docker compose $compose_files down --volumes --remove-orphans >/dev/null 2>&1 || true
}
trap cleanup EXIT INT TERM

docker compose $compose_files build intake-gate
docker compose $compose_files run --rm --no-deps intake-gate --import-legacy-profile /app/config/profile.yaml
docker compose $compose_files up --detach --force-recreate intake-gate

attempts=0
while ! curl --fail --silent http://127.0.0.1:8080/health/ready >/dev/null 2>&1; do
  attempts=$((attempts + 1))
  if [ "$attempts" -ge 60 ]; then
    echo "Timed out waiting for the real-AI test container." >&2
    exit 1
  fi
  sleep 1
done

fixture_file=$(mktemp "${TMPDIR:-/tmp}/intake-gate-real-ai.XXXXXX")
response_file=$(mktemp "${TMPDIR:-/tmp}/intake-gate-real-ai-response.XXXXXX")
audit_file=$(mktemp "${TMPDIR:-/tmp}/intake-gate-real-ai-audit.XXXXXX")
auth_cookie_jar=$(mktemp "${TMPDIR:-/tmp}/intake-gate-real-ai-auth.XXXXXX")
trap 'rm -f "$fixture_file" "$response_file" "$audit_file" "$auth_cookie_jar"; cleanup' EXIT INT TERM

csrf_json=$(curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
  http://127.0.0.1:8080/api/auth/csrf)
csrf_token=$(echo "$csrf_json" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
  --header "X-CSRF-TOKEN: $csrf_token" --header 'Content-Type: application/json' \
  --data '{"username":"smoke-admin","password":"deterministic-smoke-admin-password"}' \
  http://127.0.0.1:8080/api/auth/bootstrap >/dev/null
csrf_json=$(curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
  http://127.0.0.1:8080/api/auth/csrf)
csrf_token=$(echo "$csrf_json" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')

printf '%s\n' '{
  "workItemId": "real-ai-synthetic-1",
  "revision": "1",
  "workItemType": "Generic Request",
  "title": "Intermittent export stops before completion",
  "fields": [{"referenceName":"Generic.ExpectedBehavior","displayName":"Expected behavior","value":"All selected records are exported.","format":"plainText"}],
  "description": "The export stops after about 200 records. Timestamp 2026-09-10T14:00:00Z. password=SYNTHETIC_REAL_AI_SECRET_86420",
  "descriptionFormat": "plainText",
  "tags": ["generic"],
  "relations": [],
  "comments": [{"id":"1","createdAt":"2026-09-10T14:01:00Z","content":"Observed twice with a 500-record synthetic dataset; no error message was shown.","format":"plainText"}],
  "attachments": []
}' > "$fixture_file"

curl --fail --silent --show-error --cookie "$auth_cookie_jar" --header "X-CSRF-TOKEN: $csrf_token" \
  --header 'Content-Type: application/json' --data-binary "@$fixture_file" \
  http://127.0.0.1:8080/api/diagnostics/runs/real > "$response_file"
run_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["runId"])' "$response_file")
curl --fail --silent --show-error --cookie "$auth_cookie_jar" \
  "http://127.0.0.1:8080/api/diagnostics/runs/$run_id" > "$audit_file"

python3 - "$audit_file" "$REAL_AI_PROVIDER" "$REAL_AI_MODEL" <<'PY'
import json, sys
data = json.load(open(sys.argv[1]))
run, evaluation = data["run"], data["evaluation"]
assert run["executionMode"] == "dryRun"
assert evaluation["processingStatus"] == "completed"
assert evaluation["decision"] in ("pass", "fail")
assert evaluation["providerIdentifier"] == sys.argv[2]
assert evaluation["modelIdentifier"] == sys.argv[3]
assert not evaluation["attemptedMutations"] and not evaluation["mutationOutcomes"]
render = {
    "provider": evaluation["providerIdentifier"],
    "configuredModel": evaluation["modelIdentifier"],
    "providerReportedModel": evaluation.get("providerReportedModel"),
    "evaluationId": evaluation["evaluationId"],
    "processingStatus": evaluation["processingStatus"],
    "decision": evaluation["decision"],
    "applicableCriteria": evaluation["applicableCriteria"],
    "deficiencies": evaluation["deficiencies"],
    "ambiguities": evaluation["ambiguities"],
    "engineeringSummary": evaluation["engineeringSummary"],
    "tokenUsage": evaluation.get("tokenUsage"),
    "estimatedCost": evaluation.get("estimatedCost"),
}
print(json.dumps(render, indent=2))
PY

echo "Real-AI smoke test passed in DRY_RUN; no Azure DevOps mutation capability exists."
