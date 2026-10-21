import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { scopes, checkCommits } from './ci-scopes.mjs';

function triggered(paths, file) {
  let matches = false;
  for (const path of paths) {
    const negative = path.startsWith('!');
    const glob = negative ? path.slice(1) : path;
    const regex = new RegExp(`^${glob.replace(/[.+?^${}()|[\]\\]/g, '\\$&').replaceAll('**', '.*').replace(/(?<!\.)\*/g, '[^/]*')}$`);
    if (regex.test(file)) matches = !negative;
  }
  return matches;
}
const affected = file => scopes.filter(scope => triggered(scope.paths, file)).map(scope => scope.workflow);

test('documentation and release automation do not run application suites', () => {
  assert.deepEqual(affected('docs/operations/release-branch-cleanup.md'), []);
  assert.deepEqual(affected('.github/scripts/cleanup-released-branches.mjs'), ['release-scripts']);
  assert.deepEqual(affected('.github/workflows/release.yml'), ['release-scripts']);
  assert.deepEqual(affected('docs/architecture/example.md'), []);
});
test('code, image and monitoring each trigger the relevant workflows', () => {
  assert.deepEqual(affected('frontend/src/app/example.ts'), ['frontend', 'image']);
  assert.deepEqual(affected('backend/src/Encoding.Api/Program.cs'), ['backend', 'image']);
  assert.deepEqual(affected('docs/operations/peer-cluster-alerts.yml'), ['monitoring']);
  assert.deepEqual(affected('backend/tests/monitoring/peer-cluster-alerts.test.yml'), ['monitoring']);
  assert.deepEqual(affected('.dockerignore'), ['image']);
});
test('push and PR filters match release lookup scopes', () => {
  for (const scope of scopes) {
    const yaml = readFileSync(new URL(`../workflows/${scope.workflow}.yml`, import.meta.url), 'utf8');
    const filters = [...yaml.matchAll(/    paths:([^]*?)(?=\n\S|\n  \w)/g)];
    assert.equal(filters.length, 2, scope.workflow);
    for (const [, text] of filters) {
      const paths = [...text.matchAll(/['"]([^'"\n]+)['"]/g)].map(match => match[1]);
      assert.deepEqual(paths, scope.paths, scope.workflow);
    }
  }
});
test('release resolves checks independently and rejects missing history', () => {
  const expected = new Map(scopes.map((scope, i) => [scope.workflow, String(i + 1).repeat(40)]));
  const result = checkCommits('a'.repeat(40), (release, paths) => {
    assert.equal(release, 'a'.repeat(40));
    const scope = scopes.find(scope => scope.paths[0] === paths[0] && scope.paths.at(-1) === paths.at(-1));
    assert.ok(!paths.some(path => path.startsWith('!')));
    return expected.get(scope.workflow);
  });
  for (const scope of scopes) for (const check of scope.checks) assert.equal(result[check], expected.get(scope.workflow));
  assert.throws(() => checkCommits('a'.repeat(40), () => ''), /Missing CI source/);
});
