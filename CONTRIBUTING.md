# Development Guide

Engineering Intake Gate is a .NET 10 backend plus a stateless React frontend. The default test paths are deterministic and use fake/mock external providers.

## Toolchain

- .NET SDK `10.0.301` (see `global.json`)
- Node.js `24.21.0` and npm (see `.node-version`)
- Docker with Docker Compose v2
- `curl` and a POSIX shell for the integration harness

Product Compose itself needs only Docker; local .NET and Node installations are development conveniences.

## Backend

```bash
dotnet restore EngineeringIntakeGate.slnx
dotnet build EngineeringIntakeGate.slnx --configuration Release --no-restore
dotnet test EngineeringIntakeGate.slnx --configuration Release --no-build
dotnet format EngineeringIntakeGate.slnx --verify-no-changes --no-restore
```

Run the backend directly in its supported profileless state:

```bash
dotnet run --project src/IntakeGate.Host/IntakeGate.Host.csproj
```

## Frontend

Use the committed lockfile:

```bash
cd src/IntakeGate.Web
npm ci
npm run dev
```

The Vite development server proxies `/api` and `/health` to the backend configured in `vite.config.ts`. Run the complete frontend checks with:

```bash
npm run check
npm run audit:ci
```

When a browser-facing backend contract changes, update `openapi/intake-gate.v1.json`, regenerate `src/api/generated/schema.d.ts`, and run `npm run contracts:check`.

## Containers

Start the portfolio/product topology:

```bash
docker compose up -d --build
```

The root `docker-compose.yml` contains only the public backend and stateless UI. It starts profileless and does not load mocks, fake AI, fixtures, or developer-specific settings.

Start the deterministic test topology with mock Azure DevOps:

```bash
docker compose -f test-harness/compose/docker-compose.test.yml up -d --build
```

The test topology is not a product deployment artifact. Its mock credentials and providers are synthetic test doubles. These Compose definitions are intentionally tracked under `test-harness/compose/` because the offline release gate must remain reproducible after a fresh clone.

Machine-specific Compose customization is optional. Put it in `docker-compose.override.yml` or `docker-compose.*.local.yml`; both patterns are ignored by Git and neither is required by the product or tests.

## Release gate

Before a release candidate, run:

```bash
./test-harness/test-release-gate.sh
```

The script can use the pinned local .NET SDK or a .NET 10 SDK container. Frontend checks run in the pinned Node container. Docker image/package acquisition requires network access; the tests themselves do not call live Azure DevOps, OpenAI, or Anthropic.

Run `./test-harness/check-version.sh` for the focused static version-drift check. `VERSION` is the single human-edited product-version authority.

Generated `TestResults`, `bin`, `obj`, `node_modules`, `dist`, databases, keys, environment files, and local caches are intentionally ignored.

## License

Contributions submitted to this project are expected to be distributed under the project's [MIT License](LICENSE).

## Product constraints

- Preserve intake-completeness-only PASS semantics.
- Keep SQLite as the sole runtime profile/policy authority.
- Keep one logical profile and saved-query governance.
- Keep provider access and every mutation decision in the backend.
- Do not expose Production/LIVE without an explicit release decision.
- Keep tests deterministic and offline by default.
