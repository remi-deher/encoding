import { test } from 'node:test';
import assert from 'node:assert/strict';
import { evaluateChecks } from './wait-ci.mjs';

const run = (id, sha, conclusion, status = 'completed') => ({ id, sha, name: 'frontend-e2e', conclusion, status });
const evaluate = runs => evaluateChecks({ 'frontend-e2e': 'old' }, 'current', runs)[0].state;
test('historical failure blocks promotion even if frontend was not rerun', () => {
  assert.equal(evaluate([run(1, 'old', 'failure')]), 'failed');
});
test('current commit takes precedence and unrelated results are ignored', () => {
  assert.equal(evaluate([run(1, 'old', 'success'), run(2, 'current', 'failure')]), 'failed');
  assert.equal(evaluate([run(1, 'old', 'failure'), run(2, 'current', 'success')]), 'success');
  assert.equal(evaluate([run(1, 'unrelated', 'success')]), 'missing');
});
test('latest retry supersedes failed attempt but pending is never green', () => {
  assert.equal(evaluate([run(1, 'old', 'failure'), run(2, 'old', 'success')]), 'success');
  assert.equal(evaluate([run(1, 'old', 'success'), run(2, 'old', null, 'in_progress')]), 'pending');
});
test('cancelled, skipped, neutral, timed out and missing checks do not authorize publication', () => {
  for (const conclusion of ['cancelled', 'skipped', 'neutral', 'timed_out', 'failure']) {
    assert.equal(evaluate([run(1, 'old', conclusion)]), 'failed');
  }
  assert.equal(evaluate([]), 'missing');
});
