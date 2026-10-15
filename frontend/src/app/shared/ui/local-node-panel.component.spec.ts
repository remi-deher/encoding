import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { MatDialog } from '@angular/material/dialog';
import { LocalNodePanelComponent } from './local-node-panel.component';

const base = {
  nodeId: '11111111-1111-1111-1111-111111111111',
  name: 'nas-50',
  roles: ['server', 'worker'],
  maxConcurrentTasks: 1,
  automaticEnrollmentEnabled: false,
  capabilities: [],
  isEnrolled: false,
  pairingKey: null,
  pairingExpiresAt: null,
  clusterId: null,
  clusterName: null,
  verificationCode: null,
  switching: null,
  switchFailure: null,
};
const maison = { id: '22222222-2222-2222-2222-222222222222', name: 'Maison', apiBaseUrl: 'http://192.168.1.51:8090/', discoveredAt: '2026-10-15T18:00:00Z', source: 'manual' };

async function mount(status: object, clusters: object[] = [maison]) {
  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting(), { provide: MatDialog, useValue: { open: vi.fn() } }],
  });
  const fixture = TestBed.createComponent(LocalNodePanelComponent);
  fixture.componentRef.setInput('api', '/api/v1/local-node');
  fixture.componentRef.setInput('modeEndpoint', '/api/v1/node/mode');
  fixture.detectChanges();
  await new Promise((resolve) => setTimeout(resolve, 10));
  const http = TestBed.inject(HttpTestingController);
  http.expectOne('/api/v1/local-node/status').flush(status);
  http.expectOne('/api/v1/local-node/clusters').flush(clusters);
  fixture.detectChanges();
  return { fixture, http, element: fixture.nativeElement as HTMLElement };
}

function button(element: HTMLElement, label: string): HTMLButtonElement | undefined {
  return Array.from(element.querySelectorAll('button')).find((candidate) => candidate.textContent?.includes(label));
}

describe('LocalNodePanelComponent', () => {
  it('offers both ways of joining and a way to forget a manual address on an unattached node', async () => {
    const { element } = await mount({ ...base, standalone: true });
    expect(button(element, 'Demander à rejoindre')).toBeDefined();
    expect(button(element, 'Rejoindre directement')).toBeDefined();
    expect(button(element, 'Oublier')).toBeDefined();
  });

  it('shows the pending request with its code and hides the join list until it is cancelled', async () => {
    const { element } = await mount({ ...base, clusterId: maison.id, clusterName: 'Maison', verificationCode: 'ABC123', pairingKey: 'KEY1', pairingExpiresAt: '2026-10-15T18:10:00Z' });
    expect(element.textContent).toContain('Demande envoyée à Maison');
    expect(element.textContent).toContain('ABC123');
    expect(element.textContent).toContain('En attente d’approbation');
    expect(button(element, 'Annuler la demande')).toBeDefined();
    expect(button(element, 'Rejoindre directement')).toBeUndefined();
    expect(element.querySelector('#cluster-address')).toBeNull();
  });

  it('reports a failed attempt and flags a cluster that no longer answers', async () => {
    const { element } = await mount({ ...base, standalone: true, switchFailure: 'le cluster Maison a refusé l’enrôlement (HTTP 403)' }, [{ ...maison, reachable: false }]);
    expect(element.querySelector('[role="alert"]')?.textContent).toContain('Rattachement annulé');
    expect(element.textContent).toContain('Ne répond plus');
  });
});
