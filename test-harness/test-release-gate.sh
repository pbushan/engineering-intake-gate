#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repository_root"

if ! command -v dotnet >/dev/null 2>&1; then
    echo "Host dotnet not found; using the repository SDK container (package/image acquisition only)."
    dotnet() {
        docker run --rm \
            -v "$repository_root:/source" \
            -v intake-gate-nuget:/nuget \
            -e NUGET_PACKAGES=/nuget \
            -w /source \
            mcr.microsoft.com/dotnet/sdk:10.0 dotnet "$@"
    }
fi

results_directory="TestResults/release-gate"
mkdir -p "$results_directory"
rm -f "$results_directory/unit.trx" "$results_directory/integration.trx" \
    "$results_directory/acceptance.trx" "$results_directory/summary.json" \
    "$results_directory/frontend-bundles.txt"

echo "[1/13] Verifying product-version authority and release metadata alignment"
./test-harness/check-version.sh

echo "[2/13] Restoring and building the complete solution"
dotnet restore EngineeringIntakeGate.slnx
dotnet build EngineeringIntakeGate.slnx --configuration Release --no-restore

echo "[3/13] Running deterministic unit tests"
dotnet test tests/IntakeGate.UnitTests/IntakeGate.UnitTests.csproj --configuration Release --no-build \
    --logger "trx;LogFileName=unit.trx" --results-directory "$results_directory"

echo "[4/13] Running deterministic integration and strategic schema-upgrade matrix tests"
dotnet test tests/IntakeGate.IntegrationTests/IntakeGate.IntegrationTests.csproj --configuration Release --no-build \
    --logger "trx;LogFileName=integration.trx" --results-directory "$results_directory"

echo "[5/13] Running deterministic acceptance, Product API authorization, and CSRF matrix tests"
dotnet test tests/IntakeGate.AcceptanceTests/IntakeGate.AcceptanceTests.csproj --configuration Release --no-build \
    --logger "trx;LogFileName=acceptance.trx" --results-directory "$results_directory"

echo "[6/13] Verifying repository formatting"
dotnet format EngineeringIntakeGate.slnx --verify-no-changes --no-restore

echo "[7/13] Auditing backend direct and transitive packages for known vulnerabilities"
vulnerability_output=$(dotnet list EngineeringIntakeGate.slnx package --vulnerable --include-transitive)
echo "$vulnerability_output"
if echo "$vulnerability_output" | grep -Eiq 'has the following vulnerable packages|Critical|High severity'; then
    echo "Known vulnerable package detected." >&2
    exit 1
fi

echo "[8/13] Installing and statically checking frontend contracts, types, and lint with pinned Node LTS"
frontend_directory="$repository_root/src/IntakeGate.Web"
frontend_npm() {
    docker run --rm \
        --user "$(id -u):$(id -g)" \
        -v "$frontend_directory:/source" \
        -w /source \
        -e NPM_CONFIG_CACHE=/tmp/npm-cache \
        node:24.21.0-alpine npm "$@"
}
frontend_npm ci
frontend_npm run contracts:check
frontend_npm run typecheck
frontend_npm run lint

echo "[9/13] Running frontend unit/component and full major-route accessibility tests"
frontend_npm test
frontend_npm run test:a11y

echo "[10/13] Building and auditing the production frontend and recording bundle sizes"
frontend_npm run build
frontend_npm audit --audit-level=high
for asset in "$frontend_directory"/dist/assets/*; do
    [ -f "$asset" ] || continue
    bytes=$(wc -c <"$asset" | tr -d ' ')
    printf '%s %s bytes\n' "$(basename "$asset")" "$bytes"
done | sort -k2nr >"$results_directory/frontend-bundles.txt"
cat "$results_directory/frontend-bundles.txt"

echo "[11/13] Enforcing source security, typed browser boundary, version, and topology release checks"
if rg -n -i 'profiles/(example|alternate)' src --glob '!**/node_modules/**' --glob '!**/dist/**'; then
    echo "A sample profile path was coupled into generic production source." >&2
    exit 1
fi
if rg -n -i '(sk-[A-Za-z0-9_-]{20,}|AIza[0-9A-Za-z_-]{20,}|gh[pousr]_[0-9A-Za-z]{20,}|-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----)' \
    src profiles Dockerfile docker-compose.yml --glob '!**/node_modules/**' --glob '!**/dist/**'; then
    echo "A credential-like value was found in production source or deployment configuration." >&2
    exit 1
fi
if rg -n '<ProjectReference[^>]+(Host|Infrastructure)' src/IntakeGate.Domain src/IntakeGate.Application; then
    echo "Domain/Application dependency direction is invalid." >&2
    exit 1
fi
if rg -n -i '(dev\.azure\.com|api\.openai\.com|api\.anthropic\.com)' src/IntakeGate.Web/src --glob '!**/test/**'; then
    echo "Frontend production code contains a direct external-provider endpoint." >&2
    exit 1
fi
if rg -n '(localStorage|sessionStorage|indexedDB)' src/IntakeGate.Web/src --glob '!**/test/**'; then
    echo "Frontend production code contains persistent browser storage access." >&2
    exit 1
fi
fetch_files=$(rg -l 'fetch\s*\(' src/IntakeGate.Web/src --glob '!**/test/**' || true)
[ "$fetch_files" = "src/IntakeGate.Web/src/api/client.ts" ] || {
    echo "Frontend production fetch calls are not confined to the single typed REST client." >&2
    echo "$fetch_files" >&2
    exit 1
}
product_services=$(docker compose config --services)
[ "$product_services" = "intake-gate
intake-gate-ui" ] || { echo "Product Compose must contain only the backend and stateless UI." >&2; exit 1; }
if rg -n -i 'mock-ado|UseDeterministicFake|FakeScenario|ASPNETCORE_ENVIRONMENT:\s*(Development|Test)' docker-compose.yml; then
    echo "Product Compose contains a test-only provider or environment dependency." >&2
    exit 1
fi
test_services=$(docker compose -f test-harness/compose/docker-compose.test.yml config --services)
[ "$test_services" = "mock-ado
intake-gate" ] || { echo "Deterministic test Compose must contain only the mock provider and backend." >&2; exit 1; }
onboarding_services=$(docker compose -f docker-compose.yml -f test-harness/compose/docker-compose.onboarding.yml config --services)
echo "$onboarding_services" | grep '^mock-ado$' >/dev/null || { echo "Test onboarding overlay did not add its explicit mock provider." >&2; exit 1; }
[ "$(echo "$onboarding_services" | wc -l | tr -d ' ')" -eq 3 ] || { echo "Test onboarding topology must contain Product Compose plus one mock." >&2; exit 1; }
[ ! -e docker-compose.product.yml ] || { echo "Retired docker-compose.product.yml still exists." >&2; exit 1; }
[ ! -e test-harness/docker-compose.onboarding.yml ] || { echo "Onboarding Compose was not isolated under test-harness/compose/." >&2; exit 1; }
if find test-harness/compose -maxdepth 1 -type f -name 'docker-compose*.yml' | grep . >/dev/null; then
    :
else
    echo "Tracked test Compose definitions are missing." >&2
    exit 1
fi

echo "[12/13] Building images and running fresh-install, restart/recovery, leakage, and governed-execution Docker harnesses"
docker compose -f test-harness/compose/docker-compose.test.yml build intake-gate mock-ado
./test-harness/test-product-startup.sh
./test-harness/test-product-onboarding.sh
INTAKE_GATE_SKIP_DOTNET=1 ./test-harness/test.sh

echo "[13/13] Validating traceability and strict Controlled Dry Run release decision"
ac_rows=$(grep -Ec '^\| AC-(0[1-9]|1[0-9]|2[0-4]) \|' docs/MVP_RELEASE_GATE.md)
edge_rows=$(grep -Ec '^\| E([1-9]|1[0-9]|20) \|' docs/MVP_RELEASE_GATE.md)
[ "$ac_rows" -eq 24 ] || { echo "Expected 24 distinct AC rows, found $ac_rows." >&2; exit 1; }
[ "$edge_rows" -eq 20 ] || { echo "Expected 20 distinct edge-case rows, found $edge_rows." >&2; exit 1; }
if grep -E '^\| (AC-|E[0-9]+ |[A-Z]+-[0-9]+).+\| (FAIL|PARTIAL|NOT_TESTED) \|' docs/MVP_RELEASE_GATE.md; then
    echo "Release matrix contains a non-passing locked requirement." >&2
    exit 1
fi
grep '^MVP RELEASE DECISION: READY_FOR_CONTROLLED_DRY_RUN$' docs/MVP_RELEASE_GATE.md >/dev/null
grep '^FIRST UI MVP: READY_FOR_CONTROLLED_DRY_RUN$' docs/MVP_RELEASE_GATE.md >/dev/null
if rg -n '\| (IMPLEMENTED|IN_PROGRESS|NOT_STARTED) \|' docs/REQUIREMENTS_TRACEABILITY.md; then
    echo "Traceability contains a required item without completed verification." >&2
    exit 1
fi

attribute_total() {
    attribute=$1
    total=0
    for result in "$results_directory/unit.trx" "$results_directory/integration.trx" "$results_directory/acceptance.trx"; do
        value=$(grep -o "$attribute=\"[0-9]*\"" "$result" | head -1 | tr -cd '0-9')
        total=$((total + value))
    done
    echo "$total"
}

tests_total=$(attribute_total total)
tests_passed=$(attribute_total passed)
tests_failed=$(attribute_total failed)
tests_skipped=$(attribute_total notExecuted)
cat >"$results_directory/summary.json" <<EOF
{
  "total": $tests_total,
  "passed": $tests_passed,
  "failed": $tests_failed,
  "skipped": $tests_skipped,
  "releaseGate": "READY_FOR_CONTROLLED_DRY_RUN",
  "firstUiMvp": "READY_FOR_CONTROLLED_DRY_RUN",
  "bundleEvidence": "frontend-bundles.txt",
  "realAiSmoke": "NOT EXECUTED",
  "realAdoReadSmoke": "NOT EXECUTED"
}
EOF

echo "Release-gate summary: total=$tests_total passed=$tests_passed failed=$tests_failed skipped=$tests_skipped result=READY_FOR_CONTROLLED_DRY_RUN"
echo "Machine-readable summary: $results_directory/summary.json"
