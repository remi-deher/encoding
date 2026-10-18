import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { NotificationService } from '../../../shared/services/notification.service';
import { ContextBannerComponent } from '../../../shared/ui/context-banner.component';

@Component({
  selector: 'app-webhooks-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ContextBannerComponent,
    MatButtonModule,
  ],
  template: `
    <app-context-banner
      label="Intégrations multimédia"
      title="Webhooks entrants et déclencheurs d’import (0032)"
      description="Les plugins d’intégration (Sonarr, Radarr, Plex, Jellyfin) s’exécutent dans un bac à sable isolé. Leurs déclencheurs d’import reçoivent les événements via des webhooks sécurisés par secret dédié."
    />

    <div class="webhooks-grid">
      <!-- Section Sonarr & Radarr -->
      <section class="webhook-card">
        <div class="card-header">
          <div class="header-titles">
            <span class="card-eyebrow">Déclencheurs d’import</span>
            <h2>Sonarr & Radarr</h2>
          </div>
          <span class="status-badge">Actif</span>
        </div>

        <p class="card-desc">
          Lorsqu'un média est téléchargé ou importé par Sonarr ou Radarr, le service peut notifier Encoding pour démarrer automatiquement le flow associé à la bibliothèque.
        </p>

        <div class="config-guide">
          <div class="guide-item">
            <span class="guide-label">URL du Webhook pour Sonarr :</span>
            <div class="copy-box">
              <code>{{ baseUrl() }}/api/v1/integrations/sonarr/webhook</code>
              <button matButton="tonal" type="button" (click)="copyText(baseUrl() + '/api/v1/integrations/sonarr/webhook')">
                Copier
              </button>
            </div>
          </div>

          <div class="guide-item">
            <span class="guide-label">URL du Webhook pour Radarr :</span>
            <div class="copy-box">
              <code>{{ baseUrl() }}/api/v1/integrations/radarr/webhook</code>
              <button matButton="tonal" type="button" (click)="copyText(baseUrl() + '/api/v1/integrations/radarr/webhook')">
                Copier
              </button>
            </div>
          </div>

          <div class="guide-item">
            <span class="guide-label">Authentification :</span>
            <p class="guide-text">
              Configurez le mot de passe dans Sonarr/Radarr correspondant au <code>webhookSecret</code> défini dans la configuration du plugin sur la page <strong>Plugins & Outils</strong>, ou passez l’en-tête HTTP <code>X-Encoding-Webhook-Secret</code>.
            </p>
          </div>
        </div>
      </section>

      <!-- Section Alertes Prometheus & Monitoring -->
      <section class="webhook-card">
        <div class="card-header">
          <div class="header-titles">
            <span class="card-eyebrow">Supervision</span>
            <h2>Alertes Prometheus & Métriques</h2>
          </div>
          <span class="status-badge">Prêt</span>
        </div>

        <p class="card-desc">
          Le cluster expose des métriques de performance et des règles d’alerte standardisées pour Prometheus et Alertmanager.
        </p>

        <div class="config-guide">
          <div class="guide-item">
            <span class="guide-label">Endpoint de métriques :</span>
            <div class="copy-box">
              <code>{{ baseUrl() }}/metrics</code>
              <button matButton="tonal" type="button" (click)="copyText(baseUrl() + '/metrics')">
                Copier
              </button>
            </div>
          </div>

          <div class="guide-item">
            <span class="guide-label">Règles d'alerte cluster :</span>
            <p class="guide-text">
              Les règles d’alerte (perte de quorum consensus, saturation des workers, partitions de réseau) sont validées par <code>promtool</code> et documentées dans <code>docs/operations/peer-cluster-alerts.yml</code>.
            </p>
          </div>
        </div>
      </section>
    </div>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-4);
    }

    .webhooks-grid {
      display: grid;
      gap: var(--space-4);
    }

    .webhook-card {
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

    .card-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
    }

    .card-header h2 {
      margin: 0;
      font-size: var(--font-title);
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

    .card-desc {
      margin: 0;
      font-size: var(--font-body-sm);
      color: var(--text-secondary);
      line-height: 1.4;
    }

    .config-guide {
      display: grid;
      gap: var(--space-3);
      margin-top: var(--space-2);
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

  protected readonly baseUrl = computed(() => {
    if (typeof window !== 'undefined' && window.location) {
      return window.location.origin;
    }
    return 'http://localhost:5000';
  });

  protected async copyText(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.notifications.show('Lien copié dans le presse-papier.');
    } catch {
      this.notifications.show('Erreur lors de la copie.');
    }
  }
}
