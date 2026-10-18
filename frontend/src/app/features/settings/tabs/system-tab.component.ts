import {
  ChangeDetectionStrategy,
  Component,
  inject,
  signal,
} from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { ThemeService } from '../../../core/theme/theme.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { ContextBannerComponent } from '../../../shared/ui/context-banner.component';

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
    ContextBannerComponent,
    MatButtonModule,
  ],
  template: `
    <app-context-banner
      label="Système et exploitation"
      title="Santé du cluster et préférences"
      description="Consultez l’état de santé des composants vitaux (base SQLite, migrations, sauvegardes de flows, stockage), configurez le thème de l’interface et inspectez l’identité du cluster."
    >
      <div banner-actions>
        <button matButton="tonal" type="button" [disabled]="refreshing()" (click)="refreshAll()">
          {{ refreshing() ? 'Actualisation…' : 'Actualiser la santé' }}
        </button>
      </div>
    </app-context-banner>

    <div class="system-grid">
      <!-- Section Thème & Affichage -->
      <section class="system-card theme-card">
        <div class="card-header">
          <span class="card-eyebrow">Interface utilisateur</span>
          <h2>Thème visuel & Apparence</h2>
        </div>
        <p class="card-desc">
          L’interface applique le design system <strong>Material Design 3 Expressive</strong> avec adaptation automatique des contrastes et des conteneurs tonaux.
        </p>

        <div class="theme-options">
          <button
            type="button"
            class="theme-choice-btn"
            [class.is-selected]="!theme.isDark()"
            (click)="theme.set('light')"
          >
            <span class="theme-icon" aria-hidden="true">☀️</span>
            <div class="theme-text">
              <strong>Thème clair</strong>
              <span>Surfaces lumineuses et tons neutres</span>
            </div>
          </button>

          <button
            type="button"
            class="theme-choice-btn"
            [class.is-selected]="theme.isDark()"
            (click)="theme.set('dark')"
          >
            <span class="theme-icon" aria-hidden="true">🌙</span>
            <div class="theme-text">
              <strong>Thème sombre</strong>
              <span>Contraste doux pour environnements sombres</span>
            </div>
          </button>
        </div>
      </section>

      <!-- Section Identité du Cluster -->
      <section class="system-card cluster-id-card">
        <div class="card-header">
          <span class="card-eyebrow">Topologie</span>
          <h2>Identité du cluster</h2>
        </div>

        @if (cluster(); as cl) {
          <div class="cluster-props">
            <div class="prop-item">
              <span class="prop-k">Nom du cluster</span>
              <span class="prop-v highlight">{{ cl.name || 'Nœud autonome' }}</span>
            </div>
            <div class="prop-item">
              <span class="prop-k">Identifiant UUID</span>
              <code class="prop-v mono">{{ cl.id }}</code>
            </div>
            <div class="prop-item">
              <span class="prop-k">Mode de fonctionnement</span>
              <span class="prop-v">
                {{ cl.standalone ? 'Nœud autonome (mono-serveur)' : 'Cluster multi-serveurs maillé (0049)' }}
              </span>
            </div>
            <div class="prop-item">
              <span class="prop-k">Jeton racine cluster</span>
              <span class="prop-v">
                {{ cl.hasClusterToken ? 'Généré et actif' : 'Non configuré' }}
              </span>
            </div>
          </div>
        } @else {
          <p class="loading-state">Chargement de l’identité…</p>
        }
      </section>

      <!-- Section Santé des Composants (Health Checks) -->
      <section class="system-card health-card">
        <div class="card-header">
          <div class="header-titles">
            <span class="card-eyebrow">Observabilité</span>
            <h2>Vérifications de santé (/health/details)</h2>
          </div>
          @if (health(); as h) {
            <span
              class="health-badge"
              [class.is-healthy]="h.status === 'healthy'"
              [class.is-degraded]="h.status === 'degraded'"
              [class.is-unhealthy]="h.status === 'unhealthy'"
            >
              {{ h.status === 'healthy' ? 'Tous les voyants sont au vert' : h.status }}
              ({{ h.totalDurationMs }} ms)
            </span>
          }
        </div>

        <div class="checks-grid">
          @for (check of health()?.checks ?? []; track check.name) {
            <article class="check-box" [class.is-healthy]="check.status === 'healthy'">
              <div class="check-top">
                <span class="check-dot"></span>
                <strong>{{ formatCheckName(check.name) }}</strong>
                <span class="check-time">{{ check.durationMs }} ms</span>
              </div>
              <p class="check-sub">{{ checkDescription(check.name) }}</p>
            </article>
          } @empty {
            <p class="loading-state">Chargement des diagnostics de santé…</p>
          }
        </div>
      </section>
    </div>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-4);
    }

    .system-grid {
      display: grid;
      gap: var(--space-4);
    }

    .system-card {
      padding: var(--space-5);
      border-radius: var(--radius-xl);
      background: var(--surface-container);
      display: grid;
      gap: var(--space-3);
    }

    .card-eyebrow {
      font-size: var(--font-label);
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--primary);
      font-weight: 700;
    }

    .card-header h2 {
      margin: 0;
      font-size: var(--font-title);
      color: var(--text-primary);
    }

    .card-desc {
      margin: 0;
      font-size: var(--font-body-sm);
      color: var(--text-secondary);
      line-height: 1.4;
    }

    .theme-options {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
      gap: var(--space-3);
      margin-top: var(--space-1);
    }

    .theme-choice-btn {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-lg);
      border: 2px solid transparent;
      background: var(--surface-container-high);
      cursor: pointer;
      text-align: left;
      transition: all 0.2s ease;
    }

    .theme-choice-btn:hover {
      background: var(--surface-container-highest);
    }

    .theme-choice-btn.is-selected {
      border-color: var(--primary);
      background: var(--primary-container);
    }

    .theme-icon {
      font-size: 24px;
    }

    .theme-text {
      display: grid;
      gap: 2px;
    }

    .theme-text strong {
      font-size: var(--font-body);
      color: var(--text-primary);
    }

    .theme-text span {
      font-size: var(--font-label);
      color: var(--text-secondary);
    }

    .cluster-props {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
      gap: var(--space-3);
    }

    .prop-item {
      display: grid;
      gap: var(--space-1);
      padding: var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container-high);
    }

    .prop-k {
      font-size: var(--font-label);
      color: var(--text-secondary);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .prop-v {
      font-size: var(--font-body);
      color: var(--text-primary);
      font-weight: 600;
    }

    .prop-v.highlight {
      color: var(--primary);
    }

    .prop-v.mono {
      font-family: ui-monospace, 'JetBrains Mono', monospace;
      font-size: var(--font-body-sm);
    }

    .health-badge {
      padding: var(--space-1) var(--space-3);
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 600;
    }

    .health-badge.is-healthy {
      background: var(--success-soft);
      color: var(--success);
    }

    .health-badge.is-degraded {
      background: var(--warning-soft);
      color: var(--warning);
    }

    .health-badge.is-unhealthy {
      background: var(--danger-soft);
      color: var(--danger);
    }

    .checks-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
      gap: var(--space-3);
    }

    .check-box {
      padding: var(--space-3) var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--surface-container-high);
      display: grid;
      gap: var(--space-1);
    }

    .check-top {
      display: flex;
      align-items: center;
      gap: var(--space-2);
    }

    .check-dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: var(--success);
    }

    .check-time {
      margin-left: auto;
      font-size: var(--font-label);
      font-family: ui-monospace, monospace;
      color: var(--text-secondary);
    }

    .check-sub {
      margin: 0;
      font-size: var(--font-label);
      color: var(--text-secondary);
    }

    .loading-state {
      margin: 0;
      color: var(--text-secondary);
      padding: var(--space-2);
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
      database: 'Base SQLite locale (0043)',
      migrations: 'Schéma & Migrations (0041)',
      cluster: 'Maillage & Consensus (0049)',
      'flow-backups': 'Sauvegardes de flows (0014)',
      'artifact-storage': 'Stockage temporaire artefacts',
      'backup-storage': 'Stockage des sauvegardes',
    };
    return map[name] ?? name;
  }

  protected checkDescription(name: string): string {
    const map: Record<string, string> = {
      database: 'Fichier SQLite local, transactions WAL sans partage NFS.',
      migrations: 'État d’application des migrations de schéma du serveur.',
      cluster: 'Connexions actives entre pairs et quorum consensus.',
      'flow-backups': 'Répertoire de versions immuables JSON persistantes.',
      'artifact-storage': 'Espace disque suffisant pour les flux d’encodage.',
      'backup-storage': 'Espace disque suffisant pour les sauvegardes de flows.',
    };
    return map[name] ?? 'Composant d’exploitation opérationnel.';
  }
}
