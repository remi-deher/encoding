import { Component } from '@angular/core';
import { LocalNodePanelComponent } from '../../shared/ui/local-node-panel.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';

/**
 * Page minimale d'un nœud sans fonction serveur : il n'y a plus d'interface de gestion à relayer, la page parle donc
 * directement à l'agent. Sur un nœud avec serveur, tout se gère depuis la page Nœuds (architecture 0044).
 * Ouverte sur le poste du nœud, l'agent est en localhost:5081 (développement et docker-compose) ; depuis un autre poste,
 * même hôte sur le port 8081, redéfinissable par `localStorage['encoding.workerAgentUrl']`.
 */
function resolveWorkerAgentRoot(): string {
  try {
    const configured = localStorage.getItem('encoding.workerAgentUrl');
    if (configured && /^https?:\/\//i.test(configured)) return configured.replace(/\/+$/, '');
  } catch { /* stockage indisponible */ }
  const host = globalThis.location?.hostname ?? 'localhost';
  const local = host === 'localhost' || host === '127.0.0.1' || host === '[::1]';
  return local ? 'http://localhost:5081' : `${globalThis.location.protocol}//${host}:8081`;
}

@Component({
  selector: 'app-worker-console-page',
  imports: [LocalNodePanelComponent, PageHeaderComponent],
  template: `
    <app-page-header title="Ce nœud" />
    <app-local-node-panel [api]="agentApi" [modeEndpoint]="agentApi + '/mode'" />
  `,
})
export class WorkerConsolePageComponent {
  protected readonly agentApi = `${resolveWorkerAgentRoot()}/api/v1/worker`;
}
