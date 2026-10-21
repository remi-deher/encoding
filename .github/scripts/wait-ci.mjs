import { execFileSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { checkCommits } from './ci-scopes.mjs';

export function evaluateChecks(sources, target, runs) {
  return Object.entries(sources).map(([name, source]) => {
    const own = runs.filter(run => run.name === name && run.sha === target);
    const matching = own.length ? own : runs.filter(run => run.name === name && run.sha === source);
    // Retries supersede previous attempts of the same check on the same commit.
    const latest = matching.sort((a, b) => b.id - a.id)[0];
    const state = !latest ? 'missing' : latest.status !== 'completed' ? 'pending'
      : latest.conclusion === 'success' ? 'success' : 'failed';
    return { name, state, sha: latest?.sha ?? source, conclusion: latest?.conclusion, url: latest?.details_url };
  });
}

export async function main() {
  const sha = process.env.CI_TARGET_SHA || process.env.GITHUB_SHA;
  const repo = process.env.GITHUB_REPOSITORY;
  if (!repo || !/^[a-f0-9]{40}$/.test(sha ?? '')) throw new Error('Repository and target SHA required');
  const git = args => execFileSync('git', args, { encoding: 'utf8' }).trim();
  const sources = checkCommits(sha, (target, paths) => git(['log', '-1', '--format=%H', target, '--', ...paths]));
  const commits = [...new Set([sha, ...Object.values(sources)])];
  const deadline = Date.now() + 40 * 60 * 1000;
  let results;
  while (Date.now() < deadline) {
    const runs = commits.flatMap(commit => {
      const pages = JSON.parse(execFileSync('gh', ['api', '--paginate', '--slurp',
        `repos/${repo}/commits/${commit}/check-runs?per_page=100`], { encoding: 'utf8' }));
      return pages.flatMap(page => page.check_runs)
        .filter(run => run.app?.slug === 'github-actions').map(run => ({ ...run, sha: commit }));
    });
    results = evaluateChecks(sources, sha, runs);
    for (const result of results.filter(result => result.state !== 'success')) {
      console.log(`${result.name}: ${result.state} (${result.conclusion ?? ''}, ${result.sha}) ${result.url ?? ''}`);
    }
    const failed = results.filter(result => result.state === 'failed');
    if (failed.length) {
      for (const result of failed) console.log(`::error::${result.name}: ${result.conclusion} ${result.url ?? ''}`);
      if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY,
        failed.map(result => `- CI bloquante : ${result.name} (${result.conclusion}) ${result.url ?? ''}\n`).join(''));
      throw new Error('CI en échec : publication/promotion refusée');
    }
    if (results.every(result => result.state === 'success')) {
      console.log('Tous les contrôles requis ont réussi.');
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 20000));
  }
  throw new Error(`Délai CI dépassé : ${results.filter(result => result.state !== 'success').map(result => result.name).join(', ')}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch(error => { console.error(`::error::${error.message}`); process.exitCode = 1; });
}
