import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const temporaryDirectory = mkdtempSync(join(tmpdir(), 'intake-gate-contracts-'));
const generated = join(temporaryDirectory, 'schema.d.ts');

execFileSync(
  process.execPath,
  [
    'node_modules/openapi-typescript/bin/cli.js',
    'openapi/intake-gate.v1.json',
    '--output',
    generated,
  ],
  { stdio: 'inherit' },
);

const committed = readFileSync('src/api/generated/schema.d.ts', 'utf8');
const candidate = readFileSync(generated, 'utf8');
if (candidate !== committed) {
  process.stderr.write(
    'Generated API types are stale. Run npm run contracts:generate and commit the result.\n',
  );
  process.exitCode = 1;
}
