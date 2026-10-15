import { MatButtonModule } from '@angular/material/button';
import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, EMPTY, forkJoin, switchMap, timer } from 'rxjs';
import { NotificationService } from '../../shared/services/notification.service';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';

const WORKER_AGENT_API = 'http://localhost:5081/api/v1/worker';

interface WorkerCluster {
  readonly id: string;
  readonly name: string;
  readonly apiBaseUrl: string;
  readonly discoveredAt: string;
}

interface WorkerStatus {
  readonly nodeId: string;
  readonly name: string;
  readonly roles: readonly string[];
  readonly maxConcurrentTasks: number;
  readonly automaticEnrollmentEnabled: boolean;
  readonly capabilities: readonly { readonly toolId: string; readonly version: string }[];
  readonly isEnrolled: boolean;
  readonly pairingKey: string | null;
  readonly pairingExpiresAt: string | null;
  readonly clusterId: string | null;
  readonly clusterName: string | null;
}

interface WorkerPairingResponse {
  readonly pairingKey: string;
  readonly expiresAt: string;
}

@Component({
  selector: 'app-worker-console-page',
  imports: [CategoryTabsComponent, MatButtonModule, PageHeaderComponent],
  templateUrl: './worker-console-page.component.html',
  styleUrl: './worker-console-page.component.scss',
})
export class WorkerConsolePageComponent {
  private readonly http = inject(HttpClient);
  private readonly notifications = inject(NotificationService);
  private readonly status = signal<WorkerStatus | null>(null);
  protected readonly clusters = signal<readonly WorkerCluster[]>([]);
  protected readonly selectedTab = signal('tasks');
  protected readonly loading = signal(true);
  protected readonly enrollingClusterId = signal<string | null>(null);
  protected readonly generatingPairingClusterId = signal<string | null>(null);
  protected readonly pairingKey = signal<string | null>(null);
  protected readonly pairingExpiresAt = signal<string | null>(null);
  protected readonly refreshError = signal(false);
  protected readonly tabOptions: readonly CategoryTabOption[] = [
    { value: 'tasks', label: 'Tâches' },
    { value: 'node', label: 'Nœud' },
  ];
  protected readonly workerStatus = computed(() => this.status());

  constructor() {
    timer(0, 5_000).pipe(
      switchMap(() => forkJoin({
        status: this.http.get<WorkerStatus>(`${WORKER_AGENT_API}/status`),
        clusters: this.http.get<readonly WorkerCluster[]>(`${WORKER_AGENT_API}/clusters`),
      }).pipe(catchError(() => {
        this.refreshError.set(true);
        this.loading.set(false);
        return EMPTY;
      }))),
      takeUntilDestroyed(),
    ).subscribe(({ status, clusters }) => {
      this.status.set(status);
      this.pairingKey.set(status.pairingKey);
      this.pairingExpiresAt.set(status.pairingExpiresAt);
      this.clusters.set(clusters);
      this.refreshError.set(false);
      this.loading.set(false);
    });
  }

  protected selectTab(tab: string): void {
    this.selectedTab.set(tab);
  }

  protected toolsLabel(worker: WorkerStatus): string {
    return worker.capabilities.length
      ? worker.capabilities.map((tool) => `${tool.toolId} ${tool.version}`).join(' · ')
      : 'Aucun outil détecté';
  }

  protected pairingExpiryLabel(value: string): string {
    return new Intl.DateTimeFormat('fr-FR', { timeStyle: 'short' }).format(new Date(value));
  }

  protected joinCluster(cluster: WorkerCluster): void {
    if (this.enrollingClusterId()) return;
    this.enrollingClusterId.set(cluster.id);
    this.http.post(`${WORKER_AGENT_API}/enroll`, { clusterId: cluster.id }).pipe(
      catchError((error: unknown) => {
        this.enrollingClusterId.set(null);
        this.notifications.show(this.getErrorMessage(error));
        return EMPTY;
      }),
      takeUntilDestroyed(),
    ).subscribe(() => {
      this.enrollingClusterId.set(null);
      this.notifications.show('Ce worker a rejoint le cluster.');
      this.refreshStatus();
    });
  }

  protected generatePairingKey(cluster: WorkerCluster): void {
    if (this.generatingPairingClusterId()) return;
    this.generatingPairingClusterId.set(cluster.id);
    this.http.post<WorkerPairingResponse>(`${WORKER_AGENT_API}/pairing-key`, { clusterId: cluster.id }).pipe(
      catchError((error: unknown) => {
        this.generatingPairingClusterId.set(null);
        this.notifications.show(this.getErrorMessage(error));
        return EMPTY;
      }),
      takeUntilDestroyed(),
    ).subscribe((pairing) => {
      this.generatingPairingClusterId.set(null);
      this.pairingKey.set(pairing.pairingKey);
      this.pairingExpiresAt.set(pairing.expiresAt);
      this.notifications.show('Clé créée. Saisissez-la dans le catalogue des nœuds du serveur.');
    });
  }

  private refreshStatus(): void {
    forkJoin({
      status: this.http.get<WorkerStatus>(`${WORKER_AGENT_API}/status`),
      clusters: this.http.get<readonly WorkerCluster[]>(`${WORKER_AGENT_API}/clusters`),
    }).subscribe(({ status, clusters }) => {
      this.status.set(status);
      this.pairingKey.set(status.pairingKey);
      this.pairingExpiresAt.set(status.pairingExpiresAt);
      this.clusters.set(clusters);
    });
  }

  private getErrorMessage(error: unknown): string {
    if (typeof error === 'object' && error !== null && 'error' in error) {
      const body = (error as { error?: { title?: string } }).error;
      if (body?.title) return body.title;
    }
    return 'Impossible de rattacher ce worker au cluster. Vérifiez la connexion et réessayez.';
  }
}
