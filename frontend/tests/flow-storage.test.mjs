import assert from 'node:assert/strict';
import test from 'node:test';
import { loadModel } from './model-loader.mjs';
const { flowPublishedVersion } = await loadModel('flow-validation');
test('a new reloaded draft has no published version', () => {
  assert.equal(
    flowPublishedVersion({ definition: { version: 1 }, draftRevision: 1, publishedVersion: null }),
    0,
  );
  assert.equal(
    flowPublishedVersion({ definition: { version: 4 }, draftRevision: 2, publishedVersion: 3 }),
    3,
  );
});
test('a published definition retains its version when draft metadata is absent or null', () => {
  assert.equal(flowPublishedVersion({ definition: { version: 3 } }), 3);
  assert.equal(
    flowPublishedVersion({
      definition: { version: 3 },
      draftRevision: null,
      publishedVersion: null,
    }),
    3,
  );
});
