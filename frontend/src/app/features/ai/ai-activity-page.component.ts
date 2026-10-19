import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { DatePipe } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AiAssistantActivity } from '../../shared/models/ai-tools';
import { AiToolsService } from '../../shared/services/ai-tools.service';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';

@Component({
  selector: 'app-ai-activity-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, MatButtonModule, MatProgressSpinnerModule, PageHeaderComponent, RouterLink],
  template: `
    <app-page-header title="Activité IA" description="Historique des demandes de l’assistant et des flows.">
      <a page-actions matButton="text" routerLink="/ai">Vue d’ensemble IA</a>
    </app-page-header>
    <section class="activity-list" aria-label="Demandes récentes">
      @if (loading()) {
        <p role="status"><mat-spinner diameter="22" /> Chargement de l’activité…</p>
      } @else if (error()) {
        <div role="alert" class="state-card">
          <p>Impossible de charger l’activité.</p>
          <button matButton="text" type="button" (click)="refresh()">Réessayer</button>
        </div>
      } @else if (items().length === 0) {
        <div class="state-card">
          <h2>Aucune demande récente</h2>
          <p>Les demandes de l’assistant apparaîtront ici.</p>
        </div>
      } @else {
        @for (item of items(); track item.requestId) {
          <article
            class="activity-row"
            [class.is-selected]="selectedRequestId() === item.requestId"
            [id]="'activity-' + item.requestId"
          >
            <div>
              <h2>{{ operationName(item.operation) }} · {{ providerName(item.provider) }} · {{ statusName(item.status) }}</h2>
              <p>
                Début : {{ item.startedAt | date: 'short' }}
                @if (item.durationMilliseconds !== null) {
                  · Durée : {{ duration(item.durationMilliseconds) }}
                }
                @if (item.errorCategory) {
                  · Catégorie : {{ errorName(item.errorCategory) }}
                }
              </p>
            </div>
            @if (item.conversationId) {
              <a
              matButton="text"
              [routerLink]="['/ai/assistant']"
              [queryParams]="{ conversation: item.conversationId }"
              >Ouvrir la conversation</a
              >
            }
            <a
              matButton="text"
              [routerLink]="['/ai/activity']"
              [queryParams]="{ request: item.requestId }"
              >Lien vers la demande</a
            >
          </article>
        }
      }
    </section>
  `,
  styles: `
    :host {
      display: block;
    }
    .activity-list {
      display: grid;
      gap: var(--space-3);
    }
    .activity-row,
    .state-card {
      padding: var(--space-4);
      border-radius: var(--radius-xl);
      background: var(--surface-container-low);
    }
    .activity-row {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: var(--space-3);
    }
    .activity-row.is-selected {
      outline: 2px solid var(--accent-strong);
      outline-offset: 2px;
    }
    h2,
    p {
      margin: 0;
    }
    h2 {
      color: var(--text-primary);
      font-size: var(--font-title-sm);
    }
    p {
      margin-top: var(--space-2);
      color: var(--text-muted);
    }
    @media (max-width: 680px) {
      .activity-row {
        align-items: flex-start;
        flex-direction: column;
      }
    }
  `,
})
export class AiActivityPageComponent {
  private readonly api = inject(AiToolsService);
  private readonly route = inject(ActivatedRoute);
  protected readonly items = signal<readonly AiAssistantActivity[]>([]);
  protected readonly selectedRequestId = signal<string | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal(false);

  constructor() {
    this.route.queryParamMap.subscribe((params) =>
      this.selectedRequestId.set(params.get('request')),
    );
    this.refresh();
  }

  protected refresh(): void {
    this.loading.set(true);
    this.error.set(false);
    this.api.listAssistantActivity().subscribe({
      next: (items) => {
        this.items.set(items);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.error.set(true);
      },
    });
  }

  protected providerName(provider: string): string {
    return (
      (
        {
          'codex-cli': 'Codex CLI',
          'claude-cli': 'Claude Code',
          ollama: 'Ollama',
          openai: 'OpenAI',
        } as Record<string, string>
      )[provider] ?? provider
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
  protected statusName(status: string): string {
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
  protected errorName(category: string): string {
    return (
      (
        {
          quota: 'quota',
          authentication: 'authentification',
          timeout: 'délai dépassé',
          'provider-error': 'fournisseur',
        } as Record<string, string>
      )[category] ?? category
    );
  }
  protected duration(milliseconds: number): string {
    return milliseconds < 1000 ? `${milliseconds} ms` : `${(milliseconds / 1000).toFixed(1)} s`;
  }
}
