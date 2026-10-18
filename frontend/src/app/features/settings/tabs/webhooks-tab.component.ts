import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { NotificationService } from '../../../shared/services/notification.service';

@Component({
  selector: 'app-webhooks-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule],
  template: `
    <div class="tab-header">
      <div class="tab-titles">
        <h2>Webhooks & Intégrations</h2>
        <p>Points d’entrée pour les déclencheurs Sonarr/Radarr et collecte Prometheus.</p>
      </div>
    </div>

    <div class="webhooks-grid">
      <!-- Section Sonarr & Radarr -->
      <section class="webhook-card">
        <div class="card-header">
          <div class="header-titles">
            <span class="card-eyebrow">Import automatique</span>
            <h3>Déclencheurs Sonarr & Radarr (0032)</h3>
          </div>
          <span class="status-badge">Actif</span>
        </div>

        <div class="config-guide">
          <div class="guide-item">
            <span class="guide-label">Webhook Sonarr</span>
            <div class="copy-box">
              <code>{{ baseUrl() }}/api/v1/integrations/sonarr/webhook</code>
              <button matButton="tonal" type="button" (click)="copyText(baseUrl() + '/api/v1/integrations/sonarr/webhook')">
                Copier
              </button>
            </div>
          </div>

          <div class="guide-item">
            <span class="guide-label">Webhook Radarr</span>
            <div class="copy-box">
              <code>{{ baseUrl() }}/api/v1/integrations/radarr/webhook</code>
              <button matButton="tonal" type="button" (click)="copyText(baseUrl() + '/api/v1/integrations/radarr/webhook')">
                Copier
              </button>
            </div>
          </div>

          <p class="guide-text">
            Configurez dans Sonarr/Radarr le mot de passe correspondant au <code>webhookSecret</code> défini dans le plugin, ou passez l’en-tête HTTP <code>X-Encoding-Webhook-Secret</code>.
          </p>
        </div>
      </section>

      <!-- Section Alertes Prometheus & Monitoring -->
      <section class="webhook-card">
        <div class="card-header">
          <div class="header-titles">
            <span class="card-eyebrow">Observabilité</span>
            <h3>Métriques Prometheus</h3>
          </div>
          <span class="status-badge">Prêt</span>
        </div>

        <div class="config-guide">
          <div class="guide-item">
            <span class="guide-label">Endpoint de métriques</span>
            <div class="copy-box">
              <code>{{ baseUrl() }}/metrics</code>
              <button matButton="tonal" type="button" (click)="copyText(baseUrl() + '/metrics')">
                Copier
              </button>
            </div>
          </div>

          <p class="guide-text">
            Règles d’alerte cluster documentées dans <code>docs/operations/peer-cluster-alerts.yml</code>.
          </p>
        </div>
      </section>
    </div>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }

    .tab-header {
      padding: var(--space-2) 0;
    }

    .tab-titles h2 {
      margin: 0;
      font-size: var(--font-title);
      color: var(--text-primary);
    }

    .tab-titles p {
      margin: var(--space-1) 0 0 0;
      font-size: var(--font-body-sm);
      color: var(--text-secondary);
    }

    .webhooks-grid {
      display: grid;
      gap: var(--space-3);
    }

    .webhook-card {
      padding: var(--space-4);
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

    .card-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
    }

    .card-header h3 {
      margin: 0;
      font-size: var(--font-body);
      font-weight: 600;
      color: var(--text-primary);
    }

    .status-badge {
      padding: var(--space-1) var(--space-3);
      border-radius: var(--radius-pill);
      background: var(--success-soft);
      color: var(--success);
      font-size: var(--font-label);
      font-weight: 600;
    }

    .config-guide {
      display: grid;
      gap: var(--space-3);
    }

    .guide-item {
      display: grid;
      gap: var(--space-1);
    }

    .guide-label {
      font-size: var(--font-label);
      font-weight: 600;
      color: var(--text-secondary);
    }

    .copy-box {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-3);
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container-high);
      overflow-x: auto;
    }

    .copy-box code {
      font-family: ui-monospace, 'JetBrains Mono', monospace;
      font-size: var(--font-body-sm);
      color: var(--text-primary);
      user-select: all;
    }

    .guide-text {
      margin: 0;
      font-size: var(--font-body-sm);
      color: var(--text-secondary);
      line-height: 1.4;
    }
  `,
})
export class WebhooksTabComponent {
  private readonly notifications = inject(NotificationService);
  private readonly clipboardService = inject(ClipboardService);

  protected readonly baseUrl = computed(() => {
    if (typeof window !== 'undefined' && window.location) {
      return window.location.origin;
    }
    return 'http://localhost:5000';
  });

  protected async copyText(text: string): Promise<void> {
    const success = await this.clipboardService.copy(text);
    if (success) {
      this.notifications.show('Lien copié dans le presse-papier.');
    } else {
      this.notifications.show('Erreur lors de la copie.');
    }
  }
}
