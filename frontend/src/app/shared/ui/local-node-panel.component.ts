import { MatButtonModule } from '@angular/material/button';
import { HttpClient } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, input, signal } from '@angular/core';
import { MatDialog } from '@angular/material/dialog';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, EMPTY, filter, forkJoin, switchMap, timer } from 'rxjs';
import { NodeModeController } from '../services/node-mode-controller';
import { NotificationService } from '../services/notification.service';
import { ConfirmationDialogComponent } from './confirmation-dialog.component';

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
  readonly verificationCode: string | null;
  readonly switching: { readonly previousClusterName: string | null; readonly deadline: string | null } | null;
  readonly switchFailure: string | null;
}

interface WorkerPairingResponse {
  readonly pairingKey: string;
  readonly expiresAt: string;
}

@Component({
  selector: 'app-local-node-panel',
  imports: [MatButtonModule],
  templateUrl: './local-node-panel.component.html',
  styleUrl: './local-node-panel.component.scss',
})
export class LocalNodePanelComponent {
  /** Base des opérations sur ce nœud : le relais du serveur (`/api/v1/local-node`) ou, sur un nœud sans serveur, l'agent lui-même. */
  readonly api = input.required<string>();
  /** Opération de changement de mode du nœud (serveur ou agent, selon l'interface ouverte). */
  readonly modeEndpoint = input.required<string>();
  /** Nombre de nœuds du cluster, quand la page le connaît : un seul nœud = nœud autonome (architecture 0038, correction du 2026-10-15). */
  readonly clusterNodeCount = input<number | null>(null);
  protected readonly standalone = computed(() => !!this.status()?.isEnrolled && this.clusterNodeCount() === 1);

  private readonly http = inject(HttpClient);
  private readonly notifications = inject(NotificationService);
  private readonly nodeMode = inject(NodeModeController);
  private readonly dialog = inject(MatDialog);
  private readonly destroyRef = inject(DestroyRef);
  private readonly status = signal<WorkerStatus | null>(null);
  protected readonly clusters = signal<readonly WorkerCluster[]>([]);
  protected readonly loading = signal(true);
  protected readonly enrollingClusterId = signal<string | null>(null);
  protected readonly generatingPairingClusterId = signal<string | null>(null);
  protected readonly pairingKey = signal<string | null>(null);
  protected readonly pairingExpiresAt = signal<string | null>(null);
  protected readonly leaving = signal(false);
  protected readonly addingCluster = signal(false);
  protected readonly clusterAddress = signal('');
  protected readonly refreshError = signal(false);
  protected readonly workerStatus = computed(() => this.status());
  /** Clusters proposés : tous ceux détectés, sauf celui auquel ce nœud est déjà rattaché. */
  protected readonly joinableClusters = computed(() => {
    const current = this.status()?.clusterId?.toLowerCase();
    return this.clusters().filter((cluster) => cluster.id.toLowerCase() !== current);
  });

  constructor() {
    toObservable(this.api).pipe(
      switchMap((api) => timer(0, 5_000).pipe(switchMap(() => forkJoin({
        status: this.http.get<WorkerStatus>(`${api}/status`),
        clusters: this.http.get<readonly WorkerCluster[]>(`${api}/clusters`),
      }).pipe(catchError(() => {
        this.refreshError.set(true);
        this.loading.set(false);
        return EMPTY;
      }))))),
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

  protected async changeMode(): Promise<void> {
    if (await this.nodeMode.change(this.modeEndpoint())) this.refreshStatus();
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
    this.confirmSwitch(cluster, () => this.enroll(cluster));
  }

  protected generatePairingKey(cluster: WorkerCluster): void {
    this.confirmSwitch(cluster, () => this.createPairingKey(cluster));
  }

  protected leaveCluster(): void {
    const worker = this.status();
    if (!worker?.clusterId || this.leaving()) return;
    if (worker.switching) {
      this.leaving.set(true);
      this.http.post(`${this.api()}/leave`, {}).pipe(
        catchError((error: unknown) => {
          this.leaving.set(false);
          this.notifications.show(this.getErrorMessage(error));
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef),
      ).subscribe(() => {
        this.leaving.set(false);
        this.notifications.show('Changement de cluster annulé.');
        this.refreshStatus();
      });
      return;
    }
    this.dialog.open(ConfirmationDialogComponent, {
      data: {
        title: 'Quitter ce cluster ?',
        message: `${worker.clusterName ?? 'Ce cluster'} retirera ce nœud. Le nœud redevient autonome et retrouve son serveur local ; ses bibliothèques locales restent intactes.`,
        confirmLabel: 'Quitter le cluster',
      },
    }).afterClosed().pipe(filter(Boolean), takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.leaving.set(true);
      this.http.post(`${this.api()}/leave`, {}).pipe(
        catchError((error: unknown) => {
          this.leaving.set(false);
          this.notifications.show(this.getErrorMessage(error));
          return EMPTY;
        }),
        takeUntilDestroyed(this.destroyRef),
      ).subscribe(() => {
        this.leaving.set(false);
        this.notifications.show('Ce nœud a quitté le cluster.');
        this.refreshStatus();
      });
    });
  }

  /** Un nœud déjà rattaché (ou en attente d'approbation) ne change de cluster qu'après confirmation explicite. */
  private confirmSwitch(cluster: WorkerCluster, action: () => void): void {
    const worker = this.status();
    if (!worker?.clusterId) {
      action();
      return;
    }
    this.dialog.open(ConfirmationDialogComponent, {
      data: {
        title: `Passer au cluster ${cluster.name} ?`,
        message: `Ce nœud quitte ${worker.clusterName ?? 'son cluster actuel'}. Le cluster d’accueil doit approuver son arrivée. Si ce nœud héberge un serveur, ce serveur s’arrête une fois l’arrivée approuvée : sa base et ses bibliothèques restent intactes (exportables depuis la page Bibliothèques avant de partir).`,
        confirmLabel: 'Changer de cluster',
      },
    }).afterClosed().pipe(filter(Boolean), takeUntilDestroyed(this.destroyRef)).subscribe(action);
  }

  private enroll(cluster: WorkerCluster): void {
    if (this.enrollingClusterId()) return;
    this.enrollingClusterId.set(cluster.id);
    this.http.post(`${this.api()}/enroll`, { clusterId: cluster.id, leaveCurrent: !!this.status()?.clusterId }).pipe(
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

  private createPairingKey(cluster: WorkerCluster): void {
    if (this.generatingPairingClusterId()) return;
    this.generatingPairingClusterId.set(cluster.id);
    this.http.post<WorkerPairingResponse>(`${this.api()}/pairing-key`, { clusterId: cluster.id, leaveCurrent: !!this.status()?.clusterId }).pipe(
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
      this.notifications.show('Clé créée. Saisissez-la dans « Ajouter un nœud » du cluster d’accueil.');
    });
  }

  protected setClusterAddress(event: Event): void {
    this.clusterAddress.set((event.target as HTMLInputElement).value);
  }

  /** Ajoute un cluster par son adresse (la découverte réseau ne traverse pas toujours les conteneurs) ; option : génère aussitôt une clé. */
  protected addClusterByAddress(generateKey: boolean): void {
    const address = this.clusterAddress().trim();
    if (!address || this.addingCluster()) return;
    this.addingCluster.set(true);
    this.http.post<WorkerCluster>(`${this.api()}/clusters`, { apiBaseUrl: address }).pipe(
      catchError((error: unknown) => {
        this.addingCluster.set(false);
        this.notifications.show(this.getErrorMessage(error));
        return EMPTY;
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe((cluster) => {
      this.addingCluster.set(false);
      this.clusterAddress.set('');
      this.notifications.show(`Cluster ${cluster.name} ajouté.`);
      this.refreshStatus();
      if (generateKey) this.generatePairingKey(cluster);
    });
  }

  private refreshStatus(): void {
    forkJoin({
      status: this.http.get<WorkerStatus>(`${this.api()}/status`),
      clusters: this.http.get<readonly WorkerCluster[]>(`${this.api()}/clusters`),
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
    return 'Impossible de rattacher ce nœud au cluster. Vérifiez la connexion et réessayez.';
  }
}
