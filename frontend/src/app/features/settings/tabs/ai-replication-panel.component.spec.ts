import { TestBed } from '@angular/core/testing';
import { AiReplicationPanelComponent } from './ai-replication-panel.component';
import { AiClusterView } from '../../../shared/models/ai-tools';

describe('AiReplicationPanelComponent', () => {
  const view: AiClusterView = {
    nodes: [
      {
        nodeId: 'a',
        name: 'nas-51',
        providers: ['claude-cli'],
        reachable: true,
        credentials: [{ provider: 'claude-cli', state: 'synced', revision: 7, syncedAt: null }],
      },
      {
        nodeId: 'b',
        name: 'worker-3',
        providers: [],
        reachable: true,
        credentials: [{ provider: 'claude-cli', state: 'synced', revision: 6, syncedAt: null }],
        reconnect: ['codex-cli'],
      },
      { nodeId: 'c', name: 'worker-4', providers: [], reachable: false },
      { nodeId: 'd', name: 'ancien', providers: [], reachable: true },
    ],
  };

  function render(): string {
    TestBed.configureTestingModule({ imports: [AiReplicationPanelComponent] });
    const fixture = TestBed.createComponent(AiReplicationPanelComponent);
    fixture.componentRef.setInput('view', view);
    fixture.detectChanges();
    return fixture.nativeElement.textContent as string;
  }

  it('distingue à jour, en attente, à reconnecter, injoignable et état inconnu avec du texte', () => {
    const text = render();
    expect(text).toContain('À jour');
    expect(text).toContain('En attente');
    expect(text).toContain('À reconnecter');
    expect(text).toContain('Injoignable');
    expect(text).toContain('État inconnu');
  });

  it('annonce un fournisseur à reconnecter et n’invente pas de réplication pour Ollama', () => {
    const text = render();
    expect(text).toContain('doit être reconnecté sur 1 nœud');
    expect(text).toContain('Local à chaque nœud');
  });
});
