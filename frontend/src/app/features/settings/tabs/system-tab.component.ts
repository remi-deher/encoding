import {
  ResourceSheetComponent,
  ResourceSheetSection,
} from '../../../shared/ui/resource-sheet.component';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatExpansionModule } from '@angular/material/expansion';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { NotificationService } from '../../../shared/services/notification.service';

interface HealthCheckEntry {
  readonly name: string;
  readonly status: 'healthy' | 'degraded' | 'unhealthy';
  readonly durationMs: number;
  readonly description?: string | null;
  readonly data?: Record<string, unknown>;
}

interface HealthDetailsResponse {
  readonly status: 'healthy' | 'degraded' | 'unhealthy';
  readonly totalDurationMs: number;
  readonly checks: readonly HealthCheckEntry[];
}

interface ClusterIdentityResponse {
  readonly id: string;
  readonly name: string;
  readonly standalone: boolean;
  readonly hasClusterToken: boolean;
}

@Component({
  selector: 'app-system-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    ResourceSheetComponent,
    MatExpansionModule,
    ConfigurationSectionComponent,
  ],
  template: `
    <app-configuration-section
      title="Santé du système"
      description="État des services sur le nœud consulté."
    >
      <button section-actions matButton="tonal" [disabled]="refreshing()" (click)="refreshAll()">
        {{ refreshing() ? 'Actualisation…' : 'Actualiser la santé' }}
      </button>
      @if (health(); as value) {
        <p class="health-overview" [class.degraded]="value.status !== 'healthy'" role="status">
          {{ healthLabel(value.status) }}
          @if (lastChecked()) {
            · Dernier contrôle : {{ lastChecked() }}
          }
        </p>
      } @else {
        <p role="status">
          {{ refreshing() ? 'Chargement des diagnostics…' : 'Diagnostics indisponibles.' }}
        </p>
      }
      <app-resource-sheet
        ariaLabel="Diagnostics système"
        title="Services"
        [sections]="healthSections()"
      />
      <mat-expansion-panel
        ><mat-expansion-panel-header
          ><mat-panel-title>Identité du cluster</mat-panel-title></mat-expansion-panel-header
        ><app-resource-sheet
          ariaLabel="Identité du cluster"
          title="Installation"
          [sections]="clusterSections()"
      /></mat-expansion-panel>
    </app-configuration-section>
  `,
  styles: `
    :host {
      display: block;
    }
    .health-overview {
      padding: var(--space-3) var(--space-4);
      background: var(--success-soft);
      color: var(--success);
      border-radius: var(--radius-md);
      margin: 0 0 var(--space-4);
    }
    .health-overview.degraded {
      background: var(--warning-soft);
      color: var(--warning);
    }
    mat-expansion-panel {
      margin-top: var(--space-4);
      background: var(--surface-container-low);
      box-shadow: none;
    }
  `,
})
export class SystemTabComponent {
  private readonly http = inject(HttpClient);
  protected readonly lastChecked = signal('');
  private readonly notifications = inject(NotificationService);

  protected readonly refreshing = signal(false);
  protected readonly cluster = signal<ClusterIdentityResponse | null>(null);
  protected readonly health = signal<HealthDetailsResponse | null>(null);

  protected readonly clusterSections = computed<readonly ResourceSheetSection[]>(() => {
    const cl = this.cluster();
    return [
      {
        id: 'identity',
        title: 'Installation',
        notice: cl ? undefined : 'Identité indisponible.',
        fields: cl
          ? [
              { label: 'Nom du cluster', value: cl.name || 'Nœud autonome' },
              { label: 'Identifiant', value: cl.id, mono: true },
              { label: 'Mode', value: cl.standalone ? 'Autonome' : 'Cluster maillé' },
            ]
          : [],
      },
    ];
  });
  protected readonly healthSections = computed<readonly ResourceSheetSection[]>(() => {
    const health = this.health();
    if (!health)
      return [{ id: 'unavailable', title: 'Diagnostics', notice: 'Diagnostics indisponibles.' }];
    return health.checks.map((check) => ({
      id: check.name,
      title: this.formatCheckName(check.name),
      caption: check.description || undefined,
      items: [
        {
          label: `${check.durationMs} ms`,
          badges: [
            {
              label: this.healthLabel(check.status),
              tone:
                check.status === 'healthy'
                  ? 'success'
                  : check.status === 'degraded'
                    ? 'warning'
                    : 'danger',
            },
          ],
        },
      ],
    }));
  });
  protected healthLabel(status: string): string {
    return status === 'healthy'
      ? 'Opérationnel'
      : status === 'degraded'
        ? 'Dégradé'
        : 'Indisponible';
  }

  constructor() {
    void this.refreshAll();
  }

  async refreshAll(): Promise<void> {
    this.refreshing.set(true);
    const outcomes = await Promise.allSettled([
      firstValueFrom(this.http.get<ClusterIdentityResponse>('/api/v1/cluster')),
      firstValueFrom(this.http.get<HealthDetailsResponse>('/health/details')),
    ]);
    const [identity, diagnostics] = outcomes;
    this.cluster.set(identity.status === 'fulfilled' ? identity.value : null);
    this.health.set(diagnostics.status === 'fulfilled' ? diagnostics.value : null);
    this.lastChecked.set(
      diagnostics.status === 'fulfilled' ? new Date().toLocaleTimeString('fr-FR') : '',
    );
    if (outcomes.some((result) => result.status === 'rejected'))
      this.notifications.show('Impossible d’actualiser certains diagnostics système.');
    this.refreshing.set(false);
  }

  protected formatCheckName(name: string): string {
    const map: Record<string, string> = {
      database: 'Base de données',
      migrations: 'Schéma de données',
      cluster: 'Maillage du cluster',
      'flow-backups': 'Sauvegardes de flows',
      'artifact-storage': 'Stockage temporaire',
      'backup-storage': 'Stockage des sauvegardes',
    };
    return map[name] ?? name;
  }
}
