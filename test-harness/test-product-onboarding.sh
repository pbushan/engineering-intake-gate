#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repository_root"

COMPOSE_PROJECT_NAME=${COMPOSE_PROJECT_NAME:-engineeringintakegate_product_onboarding_$$}
export COMPOSE_PROJECT_NAME
product_compose="docker-compose.yml"
test_overlay="test-harness/compose/docker-compose.onboarding.yml"
auth_cookie_jar=$(mktemp "${TMPDIR:-/tmp}/intake-gate-onboarding-auth.XXXXXX")
auth_csrf_token=""

compose() {
    docker compose -f "$product_compose" -f "$test_overlay" "$@"
}

cleanup() {
    compose down --volumes --remove-orphans >/dev/null 2>&1 || true
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
    compose logs >&2 || true
    echo "Timed out waiting for the Product Compose onboarding deployment." >&2
    exit 1
}

refresh_csrf() {
    csrf_json=$(curl --fail --silent --show-error --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
        http://127.0.0.1:8080/api/auth/csrf)
    auth_csrf_token=$(echo "$csrf_json" | sed -n 's/.*"token":"\([^"]*\)".*/\1/p')
    [ -n "$auth_csrf_token" ] || { echo "No antiforgery token was returned." >&2; exit 1; }
}

auth_curl() {
    curl --cookie "$auth_cookie_jar" --cookie-jar "$auth_cookie_jar" \
        --header "X-CSRF-TOKEN: $auth_csrf_token" "$@"
}

record_progress() {
    auth_curl --fail --silent --show-error --request PUT --header 'Content-Type: application/json' \
        --data "{\"step\":\"$1\"}" http://127.0.0.1:8080/api/setup/progress >/dev/null
}

service_count=$(compose config --services | wc -l | tr -d ' ')
[ "$service_count" = "3" ] || { echo "Onboarding harness expected Product Compose plus one test-only mock provider." >&2; exit 1; }

compose up --build --detach
wait_until_ready

curl --fail --silent --show-error http://127.0.0.1:8080/ | grep '<title>Engineering Intake Gate</title>' >/dev/null
curl --fail --silent --show-error http://127.0.0.1:8080/health/ready | grep '"setupStatus":"incomplete"' >/dev/null

refresh_csrf
auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"username":"onboarding-admin","displayName":"Onboarding Admin","password":"deterministic-onboarding-admin-password"}' \
    http://127.0.0.1:8080/api/auth/bootstrap >/dev/null
refresh_csrf

auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/version | \
    grep '"version":"2026.9.3"' >/dev/null

record_progress Welcome
record_progress AzureDevOps
auth_curl --fail --silent --show-error --request PUT --header 'Content-Type: application/json' \
    --data '{"organizationUrl":"http://mock-ado:8081/generic-org","project":"GenericProject"}' \
    http://127.0.0.1:8080/api/ado/settings >/dev/null
ado_credential=$(auth_curl --fail --silent --show-error --request PUT --header 'Content-Type: application/json' \
    --data '{"replacement":"synthetic-mock-ado-pat"}' \
    http://127.0.0.1:8080/api/ado/credential/local)
echo "$ado_credential" | grep '"configured":true' >/dev/null
echo "$ado_credential" | grep 'synthetic-mock-ado-pat' >/dev/null && {
    echo "Azure DevOps credential value was returned to the browser boundary." >&2
    exit 1
}
auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/ado/connection-tests | grep '"succeeded":true' >/dev/null

record_progress Ai
ai_credential_canary='SYNTH_ONBOARDING_AI_CREDENTIAL_4B'
ai_credential=$(auth_curl --fail --silent --show-error --request PUT --header 'Content-Type: application/json' \
    --data "{\"replacement\":\"$ai_credential_canary\"}" \
    http://127.0.0.1:8080/api/ai/providers/openai/credential/local)
echo "$ai_credential" | grep '"configured":true' >/dev/null
echo "$ai_credential" | grep "$ai_credential_canary" >/dev/null && {
    echo "AI credential value was returned to the browser boundary." >&2
    exit 1
}
auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/ai/providers/openai/credential-tests | grep '"succeeded":true' >/dev/null
auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/ai/providers/openai/models/discover | grep '"id":"example-model"' >/dev/null
ai_candidate=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"provider":"openai","model":"example-model"}' \
    http://127.0.0.1:8080/api/ai/model-candidates/validate)
ai_confirmation_token=$(echo "$ai_candidate" | sed -n 's/.*"confirmationToken":"\([^"]*\)".*/\1/p')
[ -n "$ai_confirmation_token" ] || { echo "AI validation did not return a confirmation token." >&2; exit 1; }
auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data "{\"confirmationToken\":\"$ai_confirmation_token\"}" \
    http://127.0.0.1:8080/api/ai/model-candidates/confirm | grep '"profileConfigured":false' >/dev/null

record_progress SystemDefaults
defaults=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/setup/defaults)
echo "$defaults" | grep '"executionMode":"DRY_RUN"' >/dev/null
echo "$defaults" | grep '"policyUrl"' >/dev/null
draft=$(auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/setup/profile-draft/initialize)
draft_revision=$(echo "$draft" | sed -n 's/.*"revision":\([0-9][0-9]*\).*/\1/p')
[ -n "$draft_revision" ] || { echo "Onboarding draft initialization did not return a revision." >&2; exit 1; }

record_progress Profile
incomplete_update=$(auth_curl --fail --silent --show-error --request PUT --header 'Content-Type: application/json' \
    --data "{\"expectedRevision\":$draft_revision,\"values\":{\"profileVersion\":null,\"policyUrl\":null,\"intakeState\":null,\"aiRuntime\":{\"timeoutSeconds\":60,\"pricing\":[]},\"schedule\":{\"enabled\":false,\"expression\":\"\",\"timezone\":null,\"initialLookback\":null},\"processing\":{\"executionMode\":\"DRY_RUN\",\"concurrency\":null,\"retries\":null,\"contentLimits\":{\"maximumTotalCharacters\":null,\"maximumComments\":null,\"maximumExtractedTextCharacters\":null},\"attachmentLimits\":{\"maximumCount\":null,\"maximumBytesPerAttachment\":null,\"maximumAggregateBytes\":null,\"maximumPdfPages\":null,\"maximumImageCount\":10,\"maximumImageBytes\":5242880,\"maximumCsvRows\":1000,\"maximumStructuredTextDepth\":32}},\"audit\":{\"retentionDays\":null},\"exclusions\":[],\"policy\":null}}" \
    http://127.0.0.1:8080/api/setup/profile-draft)
echo "$incomplete_update" | grep '"policyUrl":\["Policy URL is required."\]' >/dev/null
echo "$incomplete_update" | grep '"schedule.timezone":\["Timezone is required."\]' >/dev/null
echo "$incomplete_update" | grep '"policy.criteria":\["Add at least one intake criterion."\]' >/dev/null
echo "$incomplete_update" | grep '"policyUrl":null' >/dev/null
draft_revision=$(echo "$incomplete_update" | sed -n 's/.*"revision":\([0-9][0-9]*\).*/\1/p')
[ -n "$draft_revision" ] || { echo "Incomplete onboarding draft did not remain safely persisted." >&2; exit 1; }

draft_update=$(auth_curl --fail --silent --show-error --request PUT --header 'Content-Type: application/json' \
    --data "{\"expectedRevision\":$draft_revision,\"values\":{\"profileVersion\":\"1.0\",\"policyUrl\":\"https://example.invalid/engineering/intake-standard\",\"intakeState\":{\"validatedTag\":\"INTAKE-VALIDATED\",\"incompleteTag\":\"INTAKE-INCOMPLETE\"},\"aiRuntime\":{\"timeoutSeconds\":60,\"pricing\":[]},\"schedule\":{\"enabled\":false,\"expression\":\"\",\"timezone\":\"UTC\",\"initialLookback\":\"1.00:00:00\"},\"processing\":{\"executionMode\":\"DRY_RUN\",\"concurrency\":2,\"retries\":2,\"contentLimits\":{\"maximumTotalCharacters\":100000,\"maximumComments\":100,\"maximumExtractedTextCharacters\":75000},\"attachmentLimits\":{\"maximumCount\":20,\"maximumBytesPerAttachment\":10485760,\"maximumAggregateBytes\":52428800,\"maximumPdfPages\":200,\"maximumImageCount\":10,\"maximumImageBytes\":5242880,\"maximumCsvRows\":1000,\"maximumStructuredTextDepth\":32}},\"audit\":{\"retentionDays\":90},\"exclusions\":[],\"policy\":{\"id\":\"product-onboarding-intake\",\"version\":\"1.0\",\"criteria\":[{\"id\":\"investigation-context\",\"displayName\":\"Investigation context\",\"description\":\"Relevant evidence and context needed for Engineering to begin investigation.\",\"applicability\":\"required\",\"na\":{\"allowed\":false,\"requiresExplanation\":false},\"evaluationGuidance\":\"Assess only whether enough relevant context is present to begin investigation without avoidable clarification.\"}]}}}" \
    http://127.0.0.1:8080/api/setup/profile-draft)
draft_revision=$(echo "$draft_update" | sed -n 's/.*"revision":\([0-9][0-9]*\).*/\1/p')
[ -n "$draft_revision" ] || { echo "Onboarding draft update did not return a revision." >&2; exit 1; }

# Restart matrix: recreate only the backend while setup is still profileless.
# The old session and antiforgery token, progress, draft, and both encrypted local
# credentials must all remain usable from the shared durable application volume.
backend_container_before=$(compose ps --quiet intake-gate)
compose up --detach --no-deps --force-recreate intake-gate >/dev/null
wait_until_ready
backend_container_after=$(compose ps --quiet intake-gate)
[ "$backend_container_before" != "$backend_container_after" ] || { echo "Mid-wizard backend recreation did not replace the container." >&2; exit 1; }
auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/auth/session | grep '"role":"admin"' >/dev/null
recovered_setup=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/setup/status)
echo "$recovered_setup" | grep '"lastVisitedStep":"Profile"' >/dev/null
auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/setup/profile-draft | grep "\"revision\":$draft_revision" >/dev/null
auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/ado/connection-tests | grep '"succeeded":true' >/dev/null
auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/ai/providers/openai/credential-tests | grep '"succeeded":true' >/dev/null

record_progress SavedQuery
ado_candidate=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"organizationUrl":"http://mock-ado:8081/generic-org","project":"GenericProject","savedQuery":"http://mock-ado:8081/generic-org/GenericProject/_queries/query/11111111-1111-1111-1111-111111111111/"}' \
    http://127.0.0.1:8080/api/ado/query-candidates/validate)
echo "$ado_candidate" | grep '"totalCount":13' >/dev/null
preview_count=$(echo "$ado_candidate" | grep -o '"id":[0-9][0-9]*' | wc -l | tr -d ' ')
[ "$preview_count" = "10" ] || { echo "Saved-query preview was not capped at the first ten items." >&2; exit 1; }
ado_confirmation_token=$(echo "$ado_candidate" | sed -n 's/.*"confirmationToken":"\([^"]*\)".*/\1/p')
[ -n "$ado_confirmation_token" ] || { echo "ADO validation did not return a confirmation token." >&2; exit 1; }
auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data "{\"confirmationToken\":\"$ado_confirmation_token\"}" \
    http://127.0.0.1:8080/api/ado/query-candidates/confirm | grep '"configurationGeneration":0' >/dev/null

record_progress OptionalSchedule
record_progress ReviewFinish
review_status=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/setup/status)
echo "$review_status" | grep '"profileDetailsComplete":true' >/dev/null
echo "$review_status" | grep '"policyDetailsComplete":true' >/dev/null
echo "$review_status" | grep '"azureDevOpsSavedQueryConfirmed":true' >/dev/null
echo "$review_status" | grep '"aiModelConfigured":true' >/dev/null
echo "$review_status" | grep '"setupComplete":false' >/dev/null

finalized_profile=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data "{\"expectedDraftRevision\":$draft_revision}" \
    http://127.0.0.1:8080/api/setup/finalize)
echo "$finalized_profile" | grep '"exists":true' >/dev/null
echo "$finalized_profile" | grep '"status":"active"' >/dev/null
echo "$finalized_profile" | grep '"executionMode":"DRY_RUN"' >/dev/null
echo "$finalized_profile" | grep -E '"generationId":[1-9][0-9]*' >/dev/null

configured_status=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/setup/status)
echo "$configured_status" | grep '"setupComplete":true' >/dev/null
echo "$configured_status" | grep '"runtimeActivationCurrent":true' >/dev/null
curl --fail --silent --show-error http://127.0.0.1:8080/health/ready | grep '"setupStatus":"configured"' >/dev/null

# Operational flow: browser-facing contracts execute synchronously and persist safe detail.
auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"username":"operations-viewer","displayName":"Operations Viewer","password":"deterministic-operations-viewer-password","role":"viewer"}' \
    http://127.0.0.1:8080/api/users | grep '"role":"viewer"' >/dev/null

manual_result=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"workItemId":101,"workItemUrl":null}' http://127.0.0.1:8080/api/runs/work-items)
echo "$manual_result" | grep '"decision":"pass"' >/dev/null
echo "$manual_result" | grep '"decisionLabel":"Engineering Ready"' >/dev/null
echo "$manual_result" | grep '"effectiveMode":"dryRun"' >/dev/null
manual_run_id=$(echo "$manual_result" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p')
manual_evaluation_id=$(echo "$manual_result" | sed -n 's/.*"evaluationId":"\([^"]*\)".*/\1/p')
[ -n "$manual_run_id" ] && [ -n "$manual_evaluation_id" ] || { echo "Manual operational execution did not return persisted identities." >&2; exit 1; }

# Deterministic outcome fixtures use only the existing Development/Test harness edges.
home_fail_body='{"workItemId":"home-fail","revision":"1","workItemType":"Generic","title":"Home fail fixture","description":"Bounded fixture"}'
first_home_fail=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data "$home_fail_body" http://127.0.0.1:8080/api/diagnostics/runs/fail)
echo "$first_home_fail" | grep '"decision":"fail"' >/dev/null || { echo "Home FAIL fixture did not persist FAIL." >&2; exit 1; }
second_home_fail=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data "$home_fail_body" http://127.0.0.1:8080/api/diagnostics/runs/fail)
echo "$second_home_fail" | grep '"decision":"fail"' >/dev/null || { echo "Repeated Home FAIL fixture did not persist FAIL." >&2; exit 1; }
not_eligible_result=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"workItemId":99,"workItemUrl":null}' http://127.0.0.1:8080/api/runs/work-items)
echo "$not_eligible_result" | grep '"decision":"notEligible"' >/dev/null || { echo "Home NOT_ELIGIBLE fixture was not preserved." >&2; exit 1; }

run_history=$(auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/runs?page=1&pageSize=20')
echo "$run_history" | grep "$manual_run_id" >/dev/null
manual_detail=$(auth_curl --fail --silent --show-error "http://127.0.0.1:8080/api/runs/$manual_run_id")
echo "$manual_detail" | grep '"engineeringReadyCount":1' >/dev/null
item_detail=$(auth_curl --fail --silent --show-error "http://127.0.0.1:8080/api/runs/$manual_run_id/items/$manual_evaluation_id")
echo "$item_detail" | grep '"proposedEffects":\[' >/dev/null
echo "$item_detail" | grep '"actualEffects":\[\]' >/dev/null
echo "$item_detail" | grep '"tokenUsage"' >/dev/null
echo "$item_detail" | grep '"estimatedCost"' >/dev/null

profile_run=$(auth_curl --fail --silent --show-error --request POST http://127.0.0.1:8080/api/runs)
echo "$profile_run" | grep '"invocationType":"manualIncremental"' >/dev/null
profile_run_id=$(echo "$profile_run" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p')
[ -n "$profile_run_id" ] || { echo "Run Profile Now did not return a run identity." >&2; exit 1; }
auth_curl --fail --silent --show-error "http://127.0.0.1:8080/api/runs/$profile_run_id" | grep '"summary"' >/dev/null

# Supportability flow: reads are persisted-state only; network tests are explicit.
system_health=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/support/health)
echo "$system_health" | grep '"application":{"status":"healthy"' >/dev/null
echo "$system_health" | grep '"database":{"status":"healthy"' >/dev/null
echo "$system_health" | grep '"setup":{"status":"ready"' >/dev/null
echo "$system_health" | grep '"runtime":{"status":"active"' >/dev/null
echo "$system_health" | grep '"scheduler":{"status":"manualOnly"' >/dev/null
echo "$system_health" | grep '"lastVerifiedAtUtc"' >/dev/null
echo "$system_health" | grep '"aiDiagnostics":{' >/dev/null

auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/ado/connection-tests | grep '"succeeded":true' >/dev/null
auth_curl --fail --silent --show-error --request POST \
    http://127.0.0.1:8080/api/ai/providers/openai/credential-tests | grep '"succeeded":true' >/dev/null
updated_health=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/support/health)
echo "$updated_health" | grep '"azureDevOps":{"status":"verified"' >/dev/null
echo "$updated_health" | grep '"ai":{"status":"verified"' >/dev/null

support_failure=$(auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"workItemId":"support-failure","revision":"1","workItemType":"Generic","title":"Supportability failure fixture"}' \
    http://127.0.0.1:8080/api/diagnostics/runs/e20)
echo "$support_failure" | grep '"processingStatus":"error"' >/dev/null
support_failure_run_id=$(echo "$support_failure" | sed -n 's/.*"runId":"\([^"]*\)".*/\1/p')
[ -n "$support_failure_run_id" ] || { echo "Supportability failure did not return a run identity." >&2; exit 1; }
auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/support/health | grep "$support_failure_run_id" >/dev/null

# Home summary flow: one bounded backend aggregate, persisted health, and recent runs.
home_summary=$(auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/home/summary?windowDays=30')
echo "$home_summary" | grep '"windowDays":30' >/dev/null
echo "$home_summary" | grep '"windowStartInclusiveUtc"' >/dev/null
echo "$home_summary" | grep '"windowEndExclusiveUtc"' >/dev/null
echo "$home_summary" | grep '"evaluatedCount":4' >/dev/null || { echo "Home evaluated count did not match deterministic fixtures." >&2; exit 1; }
echo "$home_summary" | grep '"engineeringReadyCount":1' >/dev/null || { echo "Home PASS count did not match deterministic fixtures." >&2; exit 1; }
echo "$home_summary" | grep '"intakeIncompleteCount":2' >/dev/null || { echo "Home FAIL count did not match deterministic fixtures." >&2; exit 1; }
echo "$home_summary" | grep '"errorCount":1' >/dev/null || { echo "Home ERROR count did not match deterministic fixtures." >&2; exit 1; }
echo "$home_summary" | grep '"notEligibleCount":1' >/dev/null || { echo "Home NOT_ELIGIBLE count did not match deterministic fixtures." >&2; exit 1; }
echo "$home_summary" | grep '"duplicateUpdatesSuppressedCount":0' >/dev/null || { echo "Home inferred suppression from repeated Controlled Dry Run assessments." >&2; exit 1; }
echo "$home_summary" | grep '"engineeringReadyRate":{"numerator":1,"denominator":3,"percentage":33.3}' >/dev/null || { echo "Home Engineering-Ready Rate did not equal PASS/(PASS+FAIL)." >&2; exit 1; }
echo "$home_summary" | grep '"estimatedAiCost":{"amount":null,"currency":null,"evaluationsWithEstimate":0,"evaluationsWithoutEstimate":4,"complete":false}' >/dev/null || { echo "Home missing historical cost coverage was not represented truthfully." >&2; exit 1; }
echo "$home_summary" | grep "$manual_run_id" >/dev/null
echo "$home_summary" | grep "$support_failure_run_id" >/dev/null
auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/home/summary?windowDays=7' | grep '"windowDays":7' >/dev/null
auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/home/summary?windowDays=90' | grep '"windowDays":90' >/dev/null
invalid_home_status=$(auth_curl --silent --output /dev/null --write-out '%{http_code}' 'http://127.0.0.1:8080/api/home/summary?windowDays=14')
[ "$invalid_home_status" = "400" ] || { echo "Home summary accepted an unsupported time window." >&2; exit 1; }

audit_history=$(auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/audit?page=1&pageSize=20&targetCategory=AzureDevOps')
echo "$audit_history" | grep '"totalCount":' >/dev/null
echo "$audit_history" | grep 'AzureDevOpsConnectionVerificationSucceeded' >/dev/null
complete_audit=$(auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/audit?page=1&pageSize=100')
echo "$complete_audit" | grep 'BootstrapAdmin' >/dev/null
echo "$complete_audit" | grep 'UserCreated' >/dev/null
echo "$complete_audit" | grep 'RunProfileNowCompleted' >/dev/null

auth_curl --fail --silent --show-error --request POST http://127.0.0.1:8080/api/auth/logout >/dev/null
refresh_csrf
auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"username":"operations-viewer","password":"deterministic-operations-viewer-password"}' \
    http://127.0.0.1:8080/api/auth/login | grep '"role":"viewer"' >/dev/null
refresh_csrf
auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/runs?page=1&pageSize=20' | grep "$manual_run_id" >/dev/null
auth_curl --fail --silent --show-error "http://127.0.0.1:8080/api/runs/$manual_run_id/items/$manual_evaluation_id" | grep '"decisionLabel":"Engineering Ready"' >/dev/null
viewer_health=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/support/health)
echo "$viewer_health" | grep '"application":{"status":"healthy"' >/dev/null
echo "$viewer_health" | grep '"aiDiagnostics":null' >/dev/null
auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/home/summary?windowDays=30' | grep '"engineeringReadyRate"' >/dev/null
auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/audit?page=1&pageSize=20' | grep '"items":\[' >/dev/null
viewer_run_status=$(auth_curl --silent --output /dev/null --write-out '%{http_code}' --request POST http://127.0.0.1:8080/api/runs)
[ "$viewer_run_status" = "403" ] || { echo "Viewer could trigger Run Profile Now." >&2; exit 1; }
viewer_ado_test_status=$(auth_curl --silent --output /dev/null --write-out '%{http_code}' --request POST http://127.0.0.1:8080/api/ado/connection-tests)
[ "$viewer_ado_test_status" = "403" ] || { echo "Viewer could test Azure DevOps." >&2; exit 1; }
viewer_ai_test_status=$(auth_curl --silent --output /dev/null --write-out '%{http_code}' --request POST http://127.0.0.1:8080/api/ai/providers/openai/credential-tests)
[ "$viewer_ai_test_status" = "403" ] || { echo "Viewer could verify the AI provider." >&2; exit 1; }

# Restart the configured backend in place and prove the active generation plus
# operational and audit history recover without creating another profile.
compose restart intake-gate >/dev/null
wait_until_ready
auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/auth/session | grep '"role":"viewer"' >/dev/null
auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/profile | grep '"status":"active"' >/dev/null
auth_curl --fail --silent --show-error "http://127.0.0.1:8080/api/runs/$manual_run_id/items/$manual_evaluation_id" | grep '"decisionLabel":"Engineering Ready"' >/dev/null
auth_curl --fail --silent --show-error 'http://127.0.0.1:8080/api/audit?page=1&pageSize=100' | grep 'BootstrapAdmin' >/dev/null

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
[ "$backend_container_before" = "$backend_container_after" ] || { echo "UI restart replaced the backend container." >&2; exit 1; }
auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/auth/session | grep '"role":"viewer"' >/dev/null
auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/profile | grep '"exists":true' >/dev/null
auth_curl --fail --silent --show-error http://127.0.0.1:8080/api/setup/status | grep '"setupComplete":true' >/dev/null

# Exercise distinctive credential canaries through the product, then inspect all
# durable files, backend logs, secured OpenAPI, and the static bundle for plaintext.
compose exec -T intake-gate sh -c \
    'test "$(stat -c %a /app/data/intake-gate.secret-key)" = 600 && test "$(stat -c %a /app/data/data-protection-keys)" = 700'
credential_canaries="$ai_credential_canary|synthetic-mock-ado-pat|deterministic-onboarding-admin-password|deterministic-operations-viewer-password"
if compose exec -T intake-gate sh -c \
    "find /app/data -maxdepth 2 -type f -exec grep -a -E '$credential_canaries' {} +" >/dev/null 2>&1; then
    echo "A plaintext credential canary was found in durable Product Compose files." >&2
    exit 1
fi
if compose logs intake-gate 2>&1 | grep -E "$credential_canaries" >/dev/null; then
    echo "A plaintext credential canary was found in Product Compose logs." >&2
    exit 1
fi
auth_cookie_value=$(awk '$6 == "IntakeGate.Authentication" { value = $7 } END { print value }' "$auth_cookie_jar")
[ -n "$auth_cookie_value" ] || { echo "Authenticated Product Compose cookie was not captured for leakage inspection." >&2; exit 1; }
for transient_value in "$auth_csrf_token" "$auth_cookie_value"; do
    if compose logs intake-gate 2>&1 | grep -F "$transient_value" >/dev/null; then
        echo "Authentication or antiforgery material was found in Product Compose logs." >&2
        exit 1
    fi
    if compose exec -T intake-gate sh -c \
        "find /app/data -maxdepth 2 -type f -exec grep -a -F '$transient_value' {} +" >/dev/null 2>&1; then
        echo "Authentication or antiforgery material was found in durable Product Compose files." >&2
        exit 1
    fi
done
auth_curl --fail --silent --show-error --request POST http://127.0.0.1:8080/api/auth/logout >/dev/null
refresh_csrf
auth_curl --fail --silent --show-error --header 'Content-Type: application/json' \
    --data '{"username":"onboarding-admin","password":"deterministic-onboarding-admin-password"}' \
    http://127.0.0.1:8080/api/auth/login | grep '"role":"admin"' >/dev/null
refresh_csrf
openapi=$(auth_curl --fail --silent --show-error http://127.0.0.1:8080/openapi/v1.json)
echo "$openapi" | grep -E -i 'passwordHash|ciphertext|authenticationTag|installationKey|providerRequest|providerResponse|attachmentBytes' >/dev/null && {
    echo "OpenAPI exposed a prohibited sensitive contract." >&2
    exit 1
}
compose exec -T intake-gate-ui sh -c \
    "! grep -R -E '$credential_canaries' /usr/share/nginx/html" >/dev/null

mock_requests=$(compose exec -T mock-ado wget --quiet --output-document=- http://127.0.0.1:8081/_mock/requests)
echo "$mock_requests" | grep '"mutationEndpoint":true' >/dev/null && {
    echo "Onboarding caused an Azure DevOps mutation." >&2
    exit 1
}

echo "Product onboarding passed: profileless welcome through activated Controlled Dry Run, mid-wizard backend recreation, encrypted credential recovery, governed operational Admin execution/history/detail, supportability/Home, configured-backend and UI-only restart persistence, Viewer read-only access, and product-surface leakage checks."
