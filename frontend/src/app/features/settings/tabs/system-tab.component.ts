import {
  ResourceSheetComponent,
  ResourceSheetSection,
} from '../../../shared/ui/resource-sheet.component';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { ThemeService } from '../../../core/theme/theme.service';
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
  imports: [MatButtonModule, ResourceSheetComponent],
  template: `
    <header class="tab-header">
      <div>
        <h2>Système & Santé</h2>
        <p>Affichage, identité du cluster et état des services sur ce nœud.</p>
      </div>
      <button matButton="tonal" type="button" [disabled]="refreshing()" (click)="refreshAll()">
        {{ refreshing() ? 'Actualisation…' : 'Actualiser la santé' }}
      </button>
    </header>
    <section class="appearance" aria-labelledby="theme-title">
      <div>
        <h3 id="theme-title">Thème visuel</h3>
        <p>Choisissez l’apparence de cette interface.</p>
      </div>
      <div class="theme-options" role="group" aria-label="Thème visuel">
        <button
          matButton="outlined"
          type="button"
          [attr.aria-pressed]="!theme.isDark()"
          (click)="theme.set('light')"
        >
          Thème clair
        </button>
        <button
          matButton="outlined"
          type="button"
          [attr.aria-pressed]="theme.isDark()"
          (click)="theme.set('dark')"
        >
          Thème sombre
        </button>
      </div>
    </section>
    <app-resource-sheet
      ariaLabel="Identité du cluster"
      title="Identité du cluster"
      [sections]="clusterSections()"
    />
    <app-resource-sheet
      ariaLabel="Diagnostics système"
      title="Composants d’exploitation"
      subtitle="Dernier contrôle des services de ce nœud."
      [sections]="healthSections()"
    />
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-5);
      min-width: 0;
    }
    h2,
    h3,
    p {
      margin: 0;
    }
    h2 {
      font-size: var(--font-title-lg);
    }
    h3 {
      font-size: var(--font-title);
    }
    p {
      color: var(--text-secondary);
      line-height: 1.5;
      margin-top: var(--space-2);
    }
    .tab-header,
    .appearance {
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: var(--space-4);
    }
    .appearance {
      padding: var(--space-5);
      background: var(--surface-container-low);
      border-radius: var(--radius-xl);
    }
    .theme-options {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
    }
    .theme-options button[aria-pressed='true'] {
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
  `,
})
export class SystemTabComponent {
  private readonly http = inject(HttpClient);
  protected readonly theme = inject(ThemeService);
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
          label: this.healthLabel(check.status),
          detail: `${check.durationMs} ms`,
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
    try {
      const [cl, hl] = await Promise.all([
        firstValueFrom(this.http.get<ClusterIdentityResponse>('/api/v1/cluster')),
        firstValueFrom(this.http.get<HealthDetailsResponse>('/health/details')),
      ]);
      this.cluster.set(cl);
      this.health.set(hl);
    } catch {
      this.notifications.show('Impossible d’actualiser certains diagnostics système.');
    } finally {
      this.refreshing.set(false);
    }
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
