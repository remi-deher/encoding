import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { RouterLink } from '@angular/router';
import { catchError, forkJoin, of } from 'rxjs';
import {
  AiAssistantActivity,
  AiClusterView,
  AiSettings,
  AiToolInfo,
} from '../../shared/models/ai-tools';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';

interface ProviderSummary {
  readonly id: string;
  readonly name: string;
  readonly status: string;
  readonly detail: string;
  readonly configured: boolean | null;
}

const PROVIDERS = [
  { id: 'ollama', name: 'Ollama' },
  { id: 'openai', name: 'OpenAI / API compatible' },
  { id: 'codex-cli', name: 'Codex CLI' },
  { id: 'claude-cli', name: 'Claude Code' },
] as const;

@Component({
  selector: 'app-ai-workspace-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MatButtonModule, MatProgressSpinnerModule, PageHeaderComponent, RouterLink],
  template: `
    <app-page-header
      title="Intelligence artificielle"
      description="État observé des fournisseurs IA et de leurs connexions dans le cluster."
    >
      <a page-actions matButton="filled" routerLink="/ai/assistant">Ouvrir l’assistant</a>
    </app-page-header>

    <div class="ai-workspace">
      @if (loading()) {
        <div class="loading" role="status">
          <mat-spinner diameter="28" />
          <span>Lecture de l’état IA…</span>
        </div>
      } @else {
        @if (loadFailed()) {
          <section class="notice notice-error" role="alert">
            <div>
              <h2>État IA partiellement indisponible</h2>
              <p>
                Une ou plusieurs sources n’ont pas répondu. Les données absentes restent inconnues.
              </p>
            </div>
            <button matButton="text" type="button" (click)="refresh()">Réessayer</button>
          </section>
        }

        <section class="overview-grid" aria-label="État des fournisseurs IA">
          <article class="surface-panel provider-panel">
            <div class="panel-heading">
              <div>
                <h2>Fournisseurs</h2>
                <p>Configuration locale et état des identifiants connus.</p>
              </div>
              <a matButton="text" [routerLink]="['/settings']" [queryParams]="{ tab: 'ai' }"
                >Paramètres IA</a
              >
            </div>

            @if (providersUnavailable()) {
              <p class="unknown-state">
                La liste est incomplète car une source de configuration n’a pas répondu.
              </p>
            }
            @if (providers().length > 0) {
              <div class="provider-list">
                @for (provider of providers(); track provider.id) {
                  <article class="provider-row">
                    <div class="provider-copy">
                      <div class="provider-title">
                        <h3>{{ provider.name }}</h3>
                        @if (provider.id === settings()?.defaultProvider) {
                          <span class="default-badge">Par défaut</span>
                        }
                      </div>
                      <p>{{ provider.detail }}</p>
                    </div>
                    <span
                      class="status-chip"
                      [class.status-chip_configured]="provider.configured === true"
                      [class.status-chip_unknown]="provider.configured === null"
                    >
                      {{ provider.status }}
                    </span>
                  </article>
                }
              </div>
            }
          </article>

          <article class="surface-panel cluster-panel">
            <div class="panel-heading">
              <div>
                <h2>Nœuds déclarants</h2>
                <p>Connexions signalées par les nœuds répondants.</p>
              </div>
            </div>
            @if (cluster(); as view) {
              @if (view.nodes.length === 0) {
                <div class="empty-state compact">
                  <h3>Aucun fournisseur signalé</h3>
                  <p>La source n’a retourné aucun nœud déclarant un fournisseur IA.</p>
                </div>
              } @else {
                <ul class="node-list">
                  @for (node of view.nodes; track node.nodeId) {
                    <li class="node-row">
                      <strong>{{ node.name }}</strong>
                      <span>{{ providerNames(node.providers) }}</span>
                    </li>
                  }
                </ul>
              }
              <p class="scope-note">
                Une connexion déclarée ou des identifiants reconnus ne prouvent pas qu’un appel au
                modèle a réussi. Cette vue n’effectue pas de test fournisseur.
              </p>
            } @else {
              <p class="unknown-state">État du cluster indisponible.</p>
            }
          </article>
        </section>

        <section class="surface-panel activity-panel">
          <div class="panel-heading">
            <div>
              <h2>Activité IA</h2>
              <p>Consultez l’historique de l’assistant et des opérations sur les flows.</p>
            </div>
            <a matButton="text" routerLink="/ai/activity">Voir l’activité</a>
          </div>
          @if (recentActivity() === null) {
            <p class="empty-activity">L’activité récente est indisponible.</p>
          } @else if (recentActivity()?.length === 0) {
            <p class="empty-activity">Aucune demande récente de l’assistant.</p>
          } @else {
            <ul class="recent-activity-list">
              @for (item of recentActivity()?.slice(0, 5) ?? []; track item.requestId) {
                <li>
                  <span
                    ><strong>{{ operationName(item.operation) }}</strong> ·
                    {{ providerName(item.provider) }} ·
                    {{ activityStatus(item.status) }}</span
                  >
                  <time [dateTime]="item.updatedAt">{{ item.updatedAt | date: 'short' }}</time>
                  <a [routerLink]="['/ai/activity']" [queryParams]="{ request: item.requestId }"
                    >Voir le détail</a
                  >
                </li>
              }
            </ul>
          }
        </section>
      }
    </div>
  `,
  styles: `
    :host {
      display: block;
    }
    .ai-workspace {
      display: grid;
      gap: var(--space-4);
    }
    .overview-grid {
      display: grid;
      grid-template-columns: minmax(0, 1.2fr) minmax(18rem, 0.8fr);
      align-items: start;
      gap: var(--space-4);
    }
    .surface-panel {
      min-width: 0;
      padding: var(--space-5);
      border-radius: var(--radius-2xl);
      background: var(--surface-container-low);
      box-shadow: var(--elevation-1);
    }
    .panel-heading {
      margin-bottom: var(--space-4);
      display: flex;
      align-items: flex-start;
      justify-content: space-between;
      gap: var(--space-3);
    }
    .panel-heading h2,
    .empty-state h3 {
      margin: 0;
      color: var(--text-primary);
    }
    .panel-heading h2 {
      font-size: var(--font-title-md);
      font-weight: 730;
    }
    .panel-heading p,
    .provider-copy p,
    .empty-state p,
    .scope-note,
    .unknown-state,
    .empty-activity {
      margin: 4px 0 0;
      color: var(--text-muted);
      font-size: var(--font-body);
      line-height: 1.5;
    }
    .provider-list,
    .node-list,
    .recent-activity-list {
      display: grid;
      gap: 3px;
      margin: 0;
      padding: 0;
      list-style: none;
    }
    .provider-row,
    .node-row,
    .recent-activity-list li {
      min-width: 0;
      padding: var(--space-3) var(--space-4);
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
      border-radius: var(--radius-md);
      background: var(--surface-container);
    }
    .provider-copy {
      min-width: 0;
    }
    .provider-title {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: var(--space-2);
    }
    .provider-title h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: var(--font-body);
      font-weight: 680;
    }
    .provider-copy p {
      font-size: var(--font-label);
    }
    .status-chip,
    .default-badge {
      flex: 0 0 auto;
      padding: 4px 10px;
      border-radius: var(--radius-pill);
      background: var(--surface-container-highest);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 650;
    }
    .status-chip_configured {
      background: var(--success-soft);
      color: var(--text-secondary);
    }
    .status-chip_unknown {
      background: var(--warning-soft);
      color: var(--text-secondary);
    }
    .default-badge {
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
    .node-row {
      align-items: flex-start;
    }
    .recent-activity-list li {
      flex-wrap: wrap;
    }
    .recent-activity-list time {
      color: var(--text-muted);
    }
    .recent-activity-list a {
      color: var(--accent-strong);
    }
    .node-row strong {
      color: var(--text-primary);
    }
    .node-row span {
      color: var(--text-muted);
      text-align: right;
    }
    .scope-note {
      margin-top: var(--space-4);
      font-size: var(--font-label);
    }
    .empty-state {
      padding: var(--space-5);
      border-radius: var(--radius-lg);
      background: var(--surface-container);
    }
    .empty-state p {
      margin-bottom: var(--space-3);
    }
    .empty-state.compact p {
      margin-bottom: 0;
    }
    .loading,
    .notice {
      display: flex;
      align-items: center;
      gap: var(--space-3);
    }
    .loading {
      min-height: 8rem;
      justify-content: center;
      color: var(--text-muted);
    }
    .notice {
      justify-content: space-between;
      padding: var(--space-4);
      border-radius: var(--radius-lg);
    }
    .notice-error {
      background: var(--warning-soft);
      color: var(--text-primary);
    }
    .notice h2 {
      margin: 0;
      font-size: var(--font-title-sm);
    }
    .notice p {
      margin: 4px 0 0;
    }
    .empty-activity {
      padding: var(--space-4);
      border-radius: var(--radius-md);
      background: var(--surface-container);
    }
    @media (max-width: 760px) {
      .overview-grid {
        grid-template-columns: minmax(0, 1fr);
      }
      .provider-row,
      .node-row {
        align-items: flex-start;
      }
      .provider-row {
        flex-wrap: wrap;
      }
      .node-row {
        flex-direction: column;
      }
      .node-row span {
        text-align: left;
      }
      .notice {
        align-items: flex-start;
        flex-direction: column;
      }
    }
  `,
})
export class AiWorkspacePageComponent {
  private readonly aiTools = inject(AiToolsService);
  protected readonly loading = signal(true);
  protected readonly loadFailed = signal(false);
  protected readonly tools = signal<readonly AiToolInfo[] | null>(null);
  protected readonly cluster = signal<AiClusterView | null>(null);
  protected readonly settings = signal<AiSettings | null>(null);
  protected readonly recentActivity = signal<readonly AiAssistantActivity[] | null>(null);
  protected readonly providersUnavailable = computed(
    () => this.tools() === null || this.settings() === null,
  );

  protected readonly providers = computed<readonly ProviderSummary[]>(() => {
    const toolData = this.tools();
    const settings = this.settings();
    if (toolData === null && settings === null) return [];

    return PROVIDERS.map((provider) => {
      if (provider.id === 'ollama') {
        if (!settings) return this.unknownProvider(provider.id, provider.name);
        const configured = Boolean(settings.ollamaBaseUrl.trim() && settings.ollamaModel.trim());
        return {
          id: provider.id,
          name: provider.name,
          configured,
          status: configured ? 'Valeurs présentes' : 'Valeurs manquantes',
          detail: configured
            ? 'Valeurs chargées · origine enregistrée ou défaut non distinguée'
            : 'Adresse ou modèle manquant',
        };
      }
      if (provider.id === 'openai') {
        if (!settings) return this.unknownProvider(provider.id, provider.name);
        const configured = Boolean(settings.openAiBaseUrl.trim() && settings.openAiModel.trim());
        return {
          id: provider.id,
          name: provider.name,
          configured,
          status: configured ? 'Valeurs présentes' : 'Valeurs manquantes',
          detail: configured
            ? 'Valeurs chargées · origine enregistrée ou défaut non distinguée'
            : 'Adresse ou modèle manquant',
        };
      }

      const tool = toolData?.find((item) => item.id === provider.id);
      if (!tool) return this.unknownProvider(provider.id, provider.name);
      if (!tool.isInstalled) {
        return {
          id: provider.id,
          name: provider.name,
          configured: false,
          status: 'Non installé',
          detail: 'Outil absent de ce nœud',
        };
      }
      const authLabels: Record<string, string> = {
        authorized: 'Identifiants reconnus',
        pending: 'Autorisation en attente',
        expired: 'Connexion expirée',
        failed: 'Échec de connexion',
        unconfigured: 'Non associé',
      };
      const status = authLabels[tool.authStatus.state] ?? 'État inconnu';
      return {
        id: provider.id,
        name: provider.name,
        configured: tool.authStatus.state === 'authorized',
        status,
        detail: tool.authStatus.accountName
          ? `Compte ${tool.authStatus.accountName} · disponibilité du service non vérifiée`
          : 'Outil installé · disponibilité du service non vérifiée',
      };
    });
  });

  constructor() {
    this.refresh();
  }

  protected refresh(): void {
    this.loading.set(true);
    this.loadFailed.set(false);
    forkJoin({
      tools: this.aiTools.listTools().pipe(catchError(() => of(null))),
      cluster: this.aiTools.getCluster().pipe(catchError(() => of(null))),
      settings: this.aiTools.getSettings().pipe(catchError(() => of(null))),
      activity: this.aiTools.listAssistantActivity().pipe(catchError(() => of(null))),
    }).subscribe(({ tools, cluster, settings, activity }) => {
      this.tools.set(tools);
      this.cluster.set(cluster);
      this.settings.set(settings);
      this.recentActivity.set(activity);
      this.loadFailed.set(tools === null || cluster === null || settings === null);
      this.loading.set(false);
    });
  }

  protected providerNames(ids: readonly string[]): string {
    if (!ids.length) return 'Aucun fournisseur déclaré';
    return ids.map((id) => PROVIDERS.find((provider) => provider.id === id)?.name ?? id).join(', ');
  }

  protected providerName(id: string): string {
    return PROVIDERS.find((provider) => provider.id === id)?.name ?? id;
  }

  protected activityStatus(status: string): string {
    return (
      (
        {
          running: 'En cours',
          succeeded: 'Réussie',
          failed: 'Échouée',
          cancelled: 'Annulée',
        } as Record<string, string>
      )[status] ?? status
    );
  }

  protected operationName(operation?: string): string {
    return (
      ({
        assistant: 'Assistant',
        'flow-generation': 'Génération de flow',
        'flow-refactor': 'Modification de flow',
        'flow-explain': 'Explication de flow',
      } as Record<string, string>)[operation ?? 'assistant'] ?? 'Opération IA'
    );
  }

  private unknownProvider(id: string, name: string): ProviderSummary {
    return {
      id,
      name,
      configured: null,
      status: 'État inconnu',
      detail: 'La source de configuration n’a pas répondu',
    };
  }
}
