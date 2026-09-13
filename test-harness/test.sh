#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repository_root"

# Use an isolated Compose project so acceptance never reads or deletes the operator's normal
# development volume. The temporary project and its volume are removed by the trap below.
COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME:-engineeringintakegate_acceptance_$$}
export COMPOSE_PROJECT_NAME
INTAKE_GATE_FAKE_AI_MANAGEMENT=${INTAKE_GATE_FAKE_AI_MANAGEMENT:-true}
export INTAKE_GATE_FAKE_AI_MANAGEMENT

compose_started=0
evidence_fixture=""
auth_cookie_jar=$(mktemp "${TMPDIR:-/tmp}/intake-gate-auth-cookies.XXXXXX")
auth_csrf_token=""

compose() {
    docker compose -f test-harness/compose/docker-compose.test.yml "$@"
}

cleanup() {
    if [ "$compose_started" -eq 1 ]; then
        compose down --volumes --remove-orphans >/dev/null 2>&1 || true
    fi
    if [ -n "$evidence_fixture" ]; then
        rm -f "$evidence_fixture"
    fi
    rm -f "$auth_cookie_jar"
}
trap cleanup EXIT INT TERM

wait_until_ready() {
    attempts=0
    while [ "$attempts" -lt 60 ]; do
        if curl --fail --silent --show-error http://127.0.0.1:8080/health/ready >/dev/null 2>&1; then
            return 0
        fi
        attempts=$((attempts + 1))
        sleep 1
    done

    echo "Timed out waiting for /health/ready." >&2
    compose logs intake-gate >&2 || true
    return 1
}

refresh_auth_csrf() {
    csrf_json=$(curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
        http://127.0.0.1:8080/api/auth/csrf)
    auth_csrf_token=$(echo "$csrf_json" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
    [ -n "$auth_csrf_token" ] || { echo "Authentication CSRF token was not returned." >&2; exit 1; }
}

bootstrap_auth() {
    : > "$auth_cookie_jar"
    refresh_auth_csrf
    curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
        --header "X-CSRF-TOKEN: $auth_csrf_token" --header 'Content-Type: application/json' \
        --data '{"username":"release-admin","displayName":"Release Admin","password":"deterministic-docker-admin-password"}' \
        http://127.0.0.1:8080/api/auth/bootstrap >/dev/null
    refresh_auth_csrf
}

login_auth() {
    : > "$auth_cookie_jar"
    refresh_auth_csrf
    curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
        --header "X-CSRF-TOKEN: $auth_csrf_token" --header 'Content-Type: application/json' \
        --data '{"username":"release-admin","password":"deterministic-docker-admin-password"}' \
        http://127.0.0.1:8080/api/auth/login >/dev/null
    refresh_auth_csrf
}

auth_curl() {
    curl --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
        --header "X-CSRF-TOKEN: $auth_csrf_token" "$@"
}

if [ "${INTAKE_GATE_SKIP_DOTNET:-0}" != "1" ]; then
    echo "Restoring and building solution"
    dotnet restore EngineeringIntakeGate.slnx
    dotnet build EngineeringIntakeGate.slnx --configuration Release --no-restore

    echo "Running unit tests"
    dotnet test tests/IntakeGate.UnitTests/IntakeGate.UnitTests.csproj --configuration Release --no-build

    echo "Running integration tests"
    dotnet test tests/IntakeGate.IntegrationTests/IntakeGate.IntegrationTests.csproj --configuration Release --no-build

    echo "Running acceptance tests"
    dotnet test tests/IntakeGate.AcceptanceTests/IntakeGate.AcceptanceTests.csproj --configuration Release --no-build
fi

echo "Building and starting a fresh profileless single application container"
compose build intake-gate mock-ado
compose up --detach intake-gate
compose_started=1
wait_until_ready

application_services=$(compose config --services | grep '^intake-gate$' | wc -l | tr -d ' ')
[ "$application_services" = "1" ] || {
    echo "Expected exactly one application service named intake-gate." >&2
    exit 1
}

curl --fail --silent --show-error http://127.0.0.1:8080/health/live | grep '"status":"live"' >/dev/null
profileless_health=$(curl --fail --silent --show-error http://127.0.0.1:8080/health/ready)
echo "$profileless_health" | grep '"status":"ready"' >/dev/null
echo "$profileless_health" | grep '"setupStatus":"incomplete"' >/dev/null
profileless_profile_status=$(curl --silent --output /dev/null --write-out '%{http_code}' http://127.0.0.1:8080/api/profile)
[ "$profileless_profile_status" = "401" ] || { echo "A fresh deployment exposed profile data anonymously." >&2; exit 1; }
bootstrap_auth
profileless_profile=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/profile)
echo "$profileless_profile" | grep '"exists":false' >/dev/null || { echo "An authenticated fresh deployment did not report profileless state." >&2; exit 1; }
profileless_run_status=$(auth_curl --silent --output /dev/null --write-out '%{http_code}' --request POST http://127.0.0.1:8080/api/runs)
[ "$profileless_run_status" = "503" ] || { echo "Profileless Run Now was not safely rejected." >&2; exit 1; }
profileless_ado_requests=$(curl --fail --silent --show-error http://127.0.0.1:8081/_mock/requests)
echo "$profileless_ado_requests" | grep '"authorizationScheme":"Basic"' >/dev/null && {
    echo "Profileless startup attempted Azure DevOps work." >&2
    exit 1
}
version_json=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/version)
echo "$version_json" | grep '"application":"Engineering Intake Gate"' >/dev/null
echo "$version_json" | grep '"version":"2026.9.1"' >/dev/null
echo "$version_json" | grep '"environment":"Development"' >/dev/null

echo "Explicitly importing the exact mounted legacy profile into the singleton SQLite authority"
compose stop intake-gate >/dev/null
compose run --rm --no-deps intake-gate --import-legacy-profile /app/config/profile.yaml
if compose run --rm --no-deps intake-gate --import-legacy-profile /app/config/profile.yaml >/dev/null 2>&1; then
    echo "A second legacy import unexpectedly replaced the singleton profile." >&2
    exit 1
fi
compose up --detach --no-build intake-gate
wait_until_ready
login_auth
echo "Validating and confirming the authoritative mock ADO boundary"
ado_candidate=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"organizationUrl":"http://mock-ado:8081/generic-org","project":"GenericProject","savedQuery":"http://mock-ado:8081/generic-org/GenericProject/_queries/query/11111111-1111-1111-1111-111111111111/"}' \
    http://127.0.0.1:8080/api/ado/query-candidates/validate)
echo "$ado_candidate" | grep '"totalCount":13' >/dev/null
echo "$ado_candidate" | grep '"preview":\[' >/dev/null
ado_confirmation_token=$(echo "$ado_candidate" | sed -n 's/.*"confirmationToken":"\([^"]*\)".*/\1/p')
[ -n "$ado_confirmation_token" ] || { echo "ADO validation did not return a confirmation token." >&2; exit 1; }
ado_confirmation=$(auth_curl --silent --show-error --header 'Content-Type: application/json' \
    --data "{\"confirmationToken\":\"$ado_confirmation_token\"}" \
    http://127.0.0.1:8080/api/ado/query-candidates/confirm)
echo "$ado_confirmation" | grep '"error":"ConfigurationActivationFailed"' >/dev/null
ado_connection=$(auth_curl --fail --silent --show-error --request POST http://127.0.0.1:8080/api/ado/connection-tests)
echo "$ado_connection" | grep '"succeeded":true' >/dev/null
curl --fail --silent --show-error http://127.0.0.1:8080/health/ready | grep '"setupStatus":"incomplete"' >/dev/null

echo "Configuring and dynamically activating the AI runtime through the deterministic provider boundary"
ai_credential_canary='SYNTH_DOCKER_AI_CREDENTIAL_903'
ai_credential=$(auth_curl --fail --silent --show-error --request PUT \
    --header 'Content-Type: application/json' \
    --data "{\"replacement\":\"$ai_credential_canary\"}" \
    http://127.0.0.1:8080/api/ai/providers/openai/credential/local)
echo "$ai_credential" | grep '"verificationStatus":"neverVerified"' >/dev/null
echo "$ai_credential" | grep "$ai_credential_canary" >/dev/null && {
    echo "AI credential metadata exposed the credential canary." >&2
    exit 1
}
ai_verification=$(auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/ai/providers/openai/credential-tests)
echo "$ai_verification" | grep '"succeeded":true' >/dev/null
ai_models=$(auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/ai/providers/openai/models/discover)
echo "$ai_models" | grep '"id":"example-model"' >/dev/null
ai_candidate=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"provider":"openai","model":"example-model"}' \
    http://127.0.0.1:8080/api/ai/model-candidates/validate)
ai_confirmation_token=$(echo "$ai_candidate" | sed -n 's/.*"confirmationToken":"\([^"]*\)".*/\1/p')
[ -n "$ai_confirmation_token" ] || { echo "AI validation did not return a confirmation token." >&2; exit 1; }
ai_confirmation=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data "{\"confirmationToken\":\"$ai_confirmation_token\"}" \
    http://127.0.0.1:8080/api/ai/model-candidates/confirm)
echo "$ai_confirmation" | grep '"restartRequired":false' >/dev/null
ai_settings=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/ai/settings)
echo "$ai_settings" | grep '"modelConfirmed":true' >/dev/null
echo "$ai_settings" | grep '"ready":true' >/dev/null
activated_profile=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/profile)
echo "$activated_profile" | grep '"status":"active"' >/dev/null
echo "$activated_profile" | grep -E '"generationId":[1-9][0-9]*' >/dev/null
curl --fail --silent --show-error http://127.0.0.1:8080/health/ready | grep '"setupStatus":"configured"' >/dev/null

profile_a_json=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/profile)
echo "$profile_a_json" | grep '"profileId":"example"' >/dev/null
echo "$profile_a_json" | grep '"id":"example-engineering-intake"' >/dev/null
profile_a_hash=$(echo "$profile_a_json" | sed -n 's/.*"fingerprint":"\([^"]*\)".*/\1/p')
[ -n "$profile_a_hash" ] || { echo "Profile A did not expose a policy fingerprint." >&2; exit 1; }
echo "$profile_a_json" | grep -Eiq 'authentication|apiKey|environmentVariable|ciphertext' && {
    echo "/api/profile exposed sensitive configuration metadata." >&2
    exit 1
}

echo "Executing the governed manual DRY_RUN regression through mock ADO"
eligible_json=$(auth_curl --fail --silent --show-error --request POST http://127.0.0.1:8080/api/runs/work-items/101)
echo "$eligible_json" | grep '"eligibility":"eligible"' >/dev/null
echo "$eligible_json" | grep '"processingStatus":"completed"' >/dev/null
echo "$eligible_json" | grep '"decision":"pass"' >/dev/null
echo "$eligible_json" | grep '"executionMode":"dryRun"' >/dev/null
echo "$eligible_json" | grep '"proposedMutationCount":2' >/dev/null

excluded_json=$(auth_curl --fail --silent --show-error --request POST http://127.0.0.1:8080/api/runs/work-items/103)
echo "$excluded_json" | grep '"eligibility":"notEligible"' >/dev/null
echo "$excluded_json" | grep '"exclusionReason":"ExcludedByRule:excluded-state"' >/dev/null
echo "$excluded_json" | grep '"decision":null' >/dev/null
echo "$excluded_json" | grep '"proposedMutationCount":0' >/dev/null

outside_json=$(auth_curl --fail --silent --show-error --request POST http://127.0.0.1:8080/api/runs/work-items/199)
echo "$outside_json" | grep '"eligibility":"notEligible"' >/dev/null
echo "$outside_json" | grep '"decision":null' >/dev/null
echo "$outside_json" | grep '"proposedMutationCount":0' >/dev/null

attachment_failure_json=$(auth_curl --fail --silent --show-error --request POST http://127.0.0.1:8080/api/runs/work-items/110)
echo "$attachment_failure_json" | grep '"processingStatus":"completed"' >/dev/null
attachment_failure_run_id=$(echo "$attachment_failure_json" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p')
attachment_failure_audit=$(auth_curl --fail --silent --show-error "http://127.0.0.1:8080/api/diagnostics/runs/$attachment_failure_run_id")
echo "$attachment_failure_audit" | grep '"processingStatus":"unavailable"' >/dev/null
echo "$attachment_failure_audit" | grep '"failureCategory":"AzureDevOpsServerFailure"' >/dev/null

echo "Executing the Run Now incremental workflow through the same mock ADO boundary"
incremental_json=$(auth_curl --fail --silent --show-error --request POST http://127.0.0.1:8080/api/runs)
echo "$incremental_json" | grep '"invocationType":"manualIncremental"' >/dev/null
echo "$incremental_json" | grep -E '"status":"(completed|completedWithErrors)"' >/dev/null
echo "$incremental_json" | grep '"effectiveMode":"dryRun"' >/dev/null

echo "Verifying the operational history/detail contracts"
incremental_run_id=$(echo "$incremental_json" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p')
[ -n "$incremental_run_id" ] || { echo "Run Profile Now did not return a run ID." >&2; exit 1; }
run_history=$(auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/runs?page=1&pageSize=25')
echo "$run_history" | grep '"totalCount":' >/dev/null
echo "$run_history" | grep "\"runId\":\"$incremental_run_id\"" >/dev/null
echo "$run_history" | grep '"triggeredBy":{"type":"user"' >/dev/null
run_detail=$(auth_curl --fail --silent --show-error "http://127.0.0.1:8080/api/runs/$incremental_run_id")
echo "$run_detail" | grep -E '"configurationGenerationId":[1-9][0-9]*' >/dev/null
echo "$run_detail" | grep '"actualEffects"' >/dev/null && {
    echo "Run aggregate unexpectedly exposed item-only effect data." >&2
    exit 1
}

url_analysis=$(auth_curl --fail --silent --show-error --request POST \
    --header 'Content-Type: application/json' \
    --data '{"workItemUrl":"http://mock-ado:8081/generic-org/GenericProject/_workitems/edit/101"}' \
    http://127.0.0.1:8080/api/runs/work-items)
echo "$url_analysis" | grep '"decision":"pass"' >/dev/null
echo "$url_analysis" | grep '"decisionLabel":"Engineering Ready"' >/dev/null
url_run_id=$(echo "$url_analysis" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p')
url_evaluation_id=$(echo "$url_analysis" | sed -n 's/.*"evaluationId":"\([^"]*\)".*/\1/p')
url_item_detail=$(auth_curl --fail --silent --show-error \
    "http://127.0.0.1:8080/api/runs/$url_run_id/items/$url_evaluation_id")
echo "$url_item_detail" | grep '"proposedEffects"' >/dev/null
echo "$url_item_detail" | grep '"actualEffects":\[\]' >/dev/null
echo "$url_item_detail" | grep '"azureDevOpsUrl":"http://mock-ado:8081/generic-org/GenericProject/_workitems/edit/101"' >/dev/null
echo "$url_item_detail" | grep -Eiq 'structuredPayload|providerRequestIds|attachmentProcessing|credential|ciphertext' && {
    echo "Operational item detail exposed prohibited data." >&2
    exit 1
}

ado_requests=$(curl --fail --silent --show-error http://127.0.0.1:8081/_mock/requests)
echo "$ado_requests" | grep '"authorizationScheme":"Basic"' >/dev/null
echo "$ado_requests" | grep -E '"method":"(PATCH|PUT|DELETE)"|"mutationEndpoint":true' >/dev/null && {
    echo "DRY_RUN issued a forbidden Azure DevOps mutation request." >&2
    exit 1
}
echo "$ado_requests$eligible_json$excluded_json$outside_json$attachment_failure_json$attachment_failure_audit$incremental_json" | grep 'synthetic-mock-ado-pat' >/dev/null && {
    echo "DRY_RUN exposed the mock PAT in a response or audit." >&2
    exit 1
}
compose logs intake-gate 2>&1 | grep 'synthetic-mock-ado-pat' >/dev/null && {
    echo "DRY_RUN exposed the mock PAT in logs." >&2
    exit 1
}
image_before=$(compose images -q intake-gate)

echo "Processing a deterministic sanitized evidence fixture"
evidence_fixture=$(mktemp "${TMPDIR:-/tmp}/intake-gate-evidence.XXXXXX")
oversized_description=$(awk 'BEGIN { for (i = 0; i < 75100; i++) printf "x" }')
attachment_text=$(printf '%s' 'attachment evidence password=SYNTH_DOCKER_ATTACHMENT_SECRET_902' | base64 | tr -d '\n')
attachment_json=$(printf '%s' '{"z":"last","a":"structured attachment evidence"}' | base64 | tr -d '\n')
cat >"$evidence_fixture" <<EOF
{
  "workItemId": "docker-fixture-1",
  "revision": "9",
  "workItemType": "Generic Request",
  "title": "Docker fixture",
  "fields": [
    { "referenceName": "Custom.Arbitrary", "displayName": "Arbitrary", "value": "password=SYNTH_DOCKER_PASSWORD_901", "format": "plainText" }
  ],
  "description": "<h1>Human description</h1><p>$oversized_description</p>",
  "descriptionFormat": "html",
  "tags": ["generic"],
  "relations": [],
  "comments": [
    { "id": "human", "authorDisplayName": "Person", "createdAt": "2026-01-01T00:00:00Z", "content": "Human evidence api_key=sk-test_SYNTHETICDOCKERKEY901", "format": "plainText" },
    { "id": "validator", "authorDisplayName": "Different Author", "createdAt": "2026-01-01T00:01:00Z", "content": "Generated evidence <!-- engineering-intake-gate:validatorVersion=1;evaluationId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee -->", "format": "plainText" }
  ],
  "attachments": [
    { "reference": "attachment-1", "fileName": "synthetic.txt", "contentType": "text/plain", "contentBytes": "$attachment_text" },
    { "reference": "attachment-2", "fileName": "structured.json", "contentType": "application/json", "contentBytes": "$attachment_json" },
    { "reference": "attachment-3", "fileName": "unsupported.bin", "contentType": "application/octet-stream", "contentBytes": "AAEC" }
  ]
}
EOF
evidence_json=$(auth_curl --fail --silent --show-error \
    --header 'Content-Type: application/json' \
    --data-binary "@$evidence_fixture" \
    http://127.0.0.1:8080/api/diagnostics/evidence/prepare)
echo "$evidence_json" | grep 'Human description' >/dev/null
echo "$evidence_json" | grep 'Human evidence' >/dev/null
echo "$evidence_json" | grep '\[REDACTED_SECRET\]' >/dev/null
echo "$evidence_json" | grep '"truncationOccurred":true' >/dev/null
echo "$evidence_json" | grep 'attachment evidence' >/dev/null
echo "$evidence_json" | grep 'structured attachment evidence' >/dev/null
echo "$evidence_json" | grep '"processingStatus":"unsupported"' >/dev/null
echo "$evidence_json" | grep '"attachmentContentInspected":true' >/dev/null
echo "$evidence_json" | grep -E 'SYNTH_DOCKER_PASSWORD_901|SYNTHETICDOCKERKEY901|SYNTH_DOCKER_ATTACHMENT_SECRET_902|Generated evidence' >/dev/null && {
    echo "Evidence diagnostic response exposed a synthetic secret or validator comment." >&2
    exit 1
}

echo "Evaluating the sanitized fixture through the scripted AI contract"
evaluation_pass=$(auth_curl --fail --silent --show-error \
    --header 'Content-Type: application/json' \
    --data-binary "@$evidence_fixture" \
    http://127.0.0.1:8080/api/diagnostics/evaluations/pass)
echo "$evaluation_pass" | grep '"processingStatus":"completed"' >/dev/null
echo "$evaluation_pass" | grep '"decision":"pass"' >/dev/null
echo "$evaluation_pass" | grep -E 'SYNTH_DOCKER_PASSWORD_901|SYNTHETICDOCKERKEY901' >/dev/null && {
    echo "Evaluation diagnostic response exposed a synthetic secret." >&2
    exit 1
}
evaluation_e3=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' --data-binary "@$evidence_fixture" http://127.0.0.1:8080/api/diagnostics/evaluations/e3)
echo "$evaluation_e3" | grep '"processingStatus":"error"' >/dev/null
echo "$evaluation_e3" | grep '"result":null' >/dev/null
evaluation_e20=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' --data-binary "@$evidence_fixture" http://127.0.0.1:8080/api/diagnostics/evaluations/e20)
echo "$evaluation_e20" | grep '"processingStatus":"error"' >/dev/null
echo "$evaluation_e20" | grep '"result":null' >/dev/null

echo "Executing and auditing the complete dry-run pipeline"
dry_run_json=$(auth_curl --fail --silent --show-error \
    --header 'Content-Type: application/json' \
    --data-binary "@$evidence_fixture" \
    http://127.0.0.1:8080/api/diagnostics/runs/fail)
echo "$dry_run_json" | grep '"decision":"fail"' >/dev/null
echo "$dry_run_json" | grep '"processingStatus":"completed"' >/dev/null
echo "$dry_run_json" | grep '"executionMode":"dryRun"' >/dev/null
echo "$dry_run_json" | grep '"type":"addTag","tag":"INTAKE-INCOMPLETE"' >/dev/null
echo "$dry_run_json" | grep '"type":"postComment"' >/dev/null
echo "$dry_run_json" | grep '"attemptedMutations":\[\]' >/dev/null
echo "$dry_run_json" | grep '"mutationOutcomes":\[\]' >/dev/null
echo "$dry_run_json" | grep -E 'SYNTH_DOCKER_PASSWORD_901|SYNTHETICDOCKERKEY901' >/dev/null && {
    echo "Dry-run response exposed a synthetic secret." >&2
    exit 1
}
dry_run_id=$(echo "$dry_run_json" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p')
dry_evaluation_id=$(echo "$dry_run_json" | sed -n 's/.*"evaluationId":"\([^"]*\)".*/\1/p')
[ -n "$dry_run_id" ] && [ -n "$dry_evaluation_id" ] || {
    echo "Dry-run did not return run/evaluation identifiers." >&2
    exit 1
}
echo "$dry_run_json" | grep "evaluationId=$dry_evaluation_id -->" >/dev/null
audit_before_restart=$(auth_curl --fail --silent --show-error "http://127.0.0.1:8080/api/diagnostics/runs/$dry_run_id")
echo "$audit_before_restart" | grep '"triggerType":"manualWorkItem"' >/dev/null
echo "$audit_before_restart" | grep '"policyFingerprint":"sha256:' >/dev/null
echo "$audit_before_restart" | grep '"proposedMutations":\[' >/dev/null
echo "$audit_before_restart" | grep '"attemptedMutations":\[\]' >/dev/null
echo "$audit_before_restart" | grep '"mutationOutcomes":\[\]' >/dev/null
echo "$audit_before_restart" | grep -E 'SYNTH_DOCKER_PASSWORD_901|SYNTHETICDOCKERKEY901' >/dev/null && {
    echo "Persisted audit exposed a synthetic secret." >&2
    exit 1
}

before_json=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/diagnostics/runtime-records)
before_count=$(echo "$before_json" | sed -n 's/.*"count":\([0-9][0-9]*\).*/\1/p')
[ -n "$before_count" ] && [ "$before_count" -ge 1 ] || {
    echo "Expected at least one persisted runtime record before restart." >&2
    exit 1
}
before_ids=$(echo "$before_json" | sed 's/"id":"/\
"id":"/g' | sed -n 's/^"id":"\([^"]*\)".*/\1/p')

echo "Restarting only the intake-gate application container"
compose restart intake-gate
wait_until_ready
login_auth

after_json=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/diagnostics/runtime-records)
after_count=$(echo "$after_json" | sed -n 's/.*"count":\([0-9][0-9]*\).*/\1/p')
[ -n "$after_count" ] && [ "$after_count" -gt "$before_count" ] || {
    echo "Expected restart to preserve records and add a new runtime record." >&2
    exit 1
}

for record_id in $before_ids; do
    echo "$after_json" | grep "\"id\":\"$record_id\"" >/dev/null || {
        echo "Persisted record $record_id was lost during restart." >&2
        exit 1
    }
done

curl --fail --silent --show-error http://127.0.0.1:8080/health/live >/dev/null
curl --fail --silent --show-error http://127.0.0.1:8080/health/ready >/dev/null
auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/version >/dev/null
audit_after_restart=$(auth_curl --fail --silent --show-error "http://127.0.0.1:8080/api/diagnostics/runs/$dry_run_id")
echo "$audit_after_restart" | grep "\"evaluationId\":\"$dry_evaluation_id\"" >/dev/null || {
    echo "Evaluation audit did not survive restart." >&2
    exit 1
}
echo "$audit_after_restart" | grep '"attemptedMutations":\[\]' >/dev/null
echo "$audit_after_restart" | grep '"mutationOutcomes":\[\]' >/dev/null

echo "Verifying the active generation and scheduler state were restored after restart"
restarted_ai_settings=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/ai/settings)
echo "$restarted_ai_settings" | grep '"model":"example-model"' >/dev/null
echo "$restarted_ai_settings" | grep '"modelConfirmed":true' >/dev/null
restarted_profile=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/profile)
echo "$restarted_profile" | grep '"status":"active"' >/dev/null
echo "$restarted_profile" | grep -E '"generationId":[1-9][0-9]*' >/dev/null
compose logs intake-gate 2>&1 | grep "$ai_credential_canary" >/dev/null && {
    echo "AI credential canary was exposed in application logs." >&2
    exit 1
}

echo "Importing profile B explicitly into a fresh deployment without rebuilding the image"
compose down --volumes --remove-orphans >/dev/null
INTAKE_GATE_PROFILE_DIRECTORY=../../profiles/alternate compose run --rm --no-deps intake-gate --import-legacy-profile /app/config/profile.yaml
INTAKE_GATE_PROFILE_DIRECTORY=../../profiles/alternate compose up --detach --no-build intake-gate
wait_until_ready
bootstrap_auth
profile_b_json=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/profile)
echo "$profile_b_json" | grep '"profileId":"alternate-example"' >/dev/null
echo "$profile_b_json" | grep '"id":"alternate-example-policy"' >/dev/null
profile_b_hash=$(echo "$profile_b_json" | sed -n 's/.*"fingerprint":"\([^"]*\)".*/\1/p')
[ -n "$profile_b_hash" ] && [ "$profile_a_hash" != "$profile_b_hash" ] || {
    echo "Expected profiles A and B to report different policy fingerprints." >&2
    exit 1
}
image_after=$(compose images -q intake-gate)
[ "$image_before" = "$image_after" ] || {
    echo "The application image changed during the profile portability test." >&2
    exit 1
}

echo "Importing the deterministic LIVE mock profile into another fresh deployment using the same image"
compose down --volumes --remove-orphans >/dev/null
INTAKE_GATE_PROFILE_DIRECTORY=../profiles/live compose run --rm --no-deps intake-gate --import-legacy-profile /app/config/profile.yaml
INTAKE_GATE_PROFILE_DIRECTORY=../profiles/live compose up --detach --no-build intake-gate
wait_until_ready
bootstrap_auth
live_profile=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/profile)
echo "$live_profile" | grep '"profileId":"acceptance-live"' >/dev/null
echo "$live_profile" | grep '"executionMode":"LIVE"' >/dev/null

echo "Verifying that unauthorized Production/LIVE configuration cannot activate or execute"
echo "$live_profile" | grep '"status":"activationFailed"' >/dev/null
live_status=$(auth_curl --silent --output /dev/null --write-out '%{http_code}' --request POST \
    http://127.0.0.1:8080/api/runs/work-items/101)
[ "$live_status" = "503" ] || { echo "Unauthorized LIVE generation unexpectedly executed." >&2; exit 1; }
live_body=$(auth_curl --silent --show-error --request POST http://127.0.0.1:8080/api/runs/work-items/101)
echo "$live_body" | grep '"error":"NoActiveConfigurationGeneration"' >/dev/null
live_requests=$(curl --fail --silent --show-error http://127.0.0.1:8081/_mock/requests)
echo "$live_requests" | grep -E '"method":"(PATCH|PUT|DELETE)"|"mutationEndpoint":true' >/dev/null && {
    echo "Unauthorized LIVE configuration issued a mock ADO mutation." >&2
    exit 1
}

safe_logs=$(compose logs intake-gate 2>&1)
echo "$safe_logs" | grep 'ApplicationStarted' >/dev/null
echo "$safe_logs" | grep -E 'SYNTH_MOCK_FIELD_SECRET_801|synthetic-mock-ado-pat' >/dev/null && {
    echo "LIVE logs exposed a synthetic secret or mock credential." >&2
    exit 1
}

echo "Docker acceptance passed: profileless startup, no-restart runtime activation, local AI credential runtime resolution, DRY_RUN execution, durable generation restart, profile portability, Production rejection, and leakage checks used image $image_after; runtime persistence grew from $before_count to $after_count record(s)."
