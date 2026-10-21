import { AiAssistantActivity } from '../../shared/models/ai-tools';
import { ExecutionResponse } from '../../shared/models/execution';
import { aiItem, executionGroup, executionItem } from './activity-items';

const execution = (state: ExecutionResponse['state']): ExecutionResponse => ({
  id: 'e1',
  media: 'Film.mkv',
  library: 'Films',
  flow: 'HEVC',
  flowVersion: 2,
  state,
  nodeId: 'n1',
  node: 'nœud-1',
  progress: 40,
  detail: 'Encodage',
  attempt: 1,
  revision: 1,
  queuedAt: '2026-01-01T10:00:00Z',
  startedAt: '2026-01-01T10:01:00Z',
  finishedAt: null,
  updatedAt: '2026-01-01T10:02:00Z',
});

const ai = (status: AiAssistantActivity['status']): AiAssistantActivity => ({
  requestId: 'r1',
  conversationId: null,
  provider: 'claude-cli',
  status,
  startedAt: '2026-01-01T10:00:00Z',
  updatedAt: '2026-01-01T10:00:05Z',
  durationMilliseconds: 5000,
  errorCategory: null,
});

describe('activity items', () => {
  it('groups executions by state', () => {
    expect(executionGroup('queued')).toBe('queue');
    expect(executionGroup('cancelling')).toBe('active');
    expect(executionGroup('failed')).toBe('errors');
    expect(executionGroup('deferred')).toBe('done');
  });

  it('shows progress only while running', () => {
    expect(executionItem(execution('running')).progress).toBe(40);
    expect(executionItem(execution('queued')).progress).toBeNull();
  });

  it('maps AI requests into the same groups', () => {
    expect(aiItem(ai('running')).group).toBe('active');
    expect(aiItem(ai('failed')).group).toBe('errors');
    expect(aiItem(ai('proposal-ready')).group).toBe('done');
    expect(aiItem(ai('succeeded')).key).toBe('ai:r1');
  });
});
