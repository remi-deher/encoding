import { ChangeDetectionStrategy, Component, OnDestroy, inject, signal } from '@angular/core';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { AuthService } from '../../../core/auth/auth.service';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { AiClusterModelCatalog } from '../../../shared/models/ai-tools';

@Component({
  selector: 'app-ai-model-catalog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatSlideToggleModule],
  template: `
    <section aria-labelledby="ai-models-title">
      <header>
        <h3 id="ai-models-title">Modèles disponibles</h3>
      </header>
      @if (loading()) {
        <p role="status">Lecture des catalogues fournisseurs…</p>
      }
      @if (error()) {
        <p role="alert">{{ error() }}</p>
      }
      @for (node of catalog()?.nodes ?? []; track node.nodeId) {
        <h4>{{ node.name }}</h4>
        @for (provider of node.providers; track provider.provider) {
          <article>
            <h4>{{ provider.provider }}</h4>
            @if (provider.state !== 'available') {
              <p role="status">{{ provider.message ?? 'Catalogue indisponible' }}</p>
            }
            @for (model of provider.models; track model.id) {
              <div class="model-row">
                <mat-slide-toggle
                  [checked]="enabled(provider.provider, model.id)"
                  [disabled]="!isAdmin() || saving() || provider.state !== 'available'"
                  (change)="setEnabled(provider.provider, model.id, $event.checked)"
                  >{{ model.label }}</mat-slide-toggle
                >
                <span>{{
                  model.reasoningKnown
                    ? model.reasoningEfforts.length
                      ? 'Raisonnement : ' + model.reasoningEfforts.join(', ')
                      : 'Sans niveau de raisonnement'
                    : 'Raisonnement non communiqué'
                }}</span>
              </div>
            }
          </article>
        }
      }
      @if (!loading() && !catalog()?.nodes?.length && !error()) {
        <p>Aucune connexion disponible.</p>
      }
    </section>
  `,
  styles: `
    section {
      margin-block: var(--space-5);
    }
    header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
      flex-wrap: wrap;
    }
    article {
      padding: var(--space-4);
      margin-block: var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container);
    }
    .model-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
      flex-wrap: wrap;
      padding-block: var(--space-3);
    }
    .model-row span,
    p {
      color: var(--text-muted);
      font-size: var(--font-label);
    }
  `,
})
export class AiModelCatalogComponent implements OnDestroy {
  private readonly api = inject(AiToolsService);
  private readonly notifications = inject(NotificationService);
  protected readonly isAdmin = inject(AuthService).isAdmin;
  protected readonly catalog = signal<AiClusterModelCatalog | null>(null);
  protected readonly loading = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  constructor() {
    this.refresh();
  }
  ngOnDestroy(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
  }
  protected refresh(): void {
    if (this.pollTimer !== null) clearTimeout(this.pollTimer);
    this.pollTimer = null;
    this.loading.set(true);
    this.error.set(null);
    this.api.getModelCatalog().subscribe({
      next: (catalog) => {
        this.catalog.set(catalog);
        const catalogLoading = catalog.nodes.some((node) =>
          node.providers.some((provider) => provider.state === 'loading'),
        );
        this.loading.set(catalogLoading);
        if (catalogLoading) this.pollTimer = window.setTimeout(() => this.refresh(), 750);
      },
      error: () => {
        this.loading.set(false);
        this.error.set('Impossible de lire les modèles annoncés par les nœuds.');
      },
    });
  }
  protected enabled(provider: string, model: string): boolean {
    return !this.catalog()?.policy.disabledModels.some(
      (item) => item.provider === provider && item.model === model,
    );
  }
  protected setEnabled(provider: string, model: string, enabled: boolean): void {
    this.saving.set(true);
    this.api.updateModelPolicy(provider, model, enabled).subscribe({
      next: (policy) => {
        this.catalog.update((value) => (value ? { ...value, policy } : value));
        this.saving.set(false);
        this.notifications.show(enabled ? 'Modèle activé.' : 'Modèle désactivé.');
      },
      error: () => {
        this.saving.set(false);
        this.notifications.show('Impossible de modifier ce modèle.');
        this.refresh();
      },
    });
  }
}
