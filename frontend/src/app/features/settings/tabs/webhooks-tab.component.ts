import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatExpansionModule } from '@angular/material/expansion';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { NotificationService } from '../../../shared/services/notification.service';
@Component({
  selector: 'app-webhooks-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatExpansionModule, ConfigurationSectionComponent],
  template: `
    <app-configuration-section
      title="Webhooks"
      description="Déclenchez la découverte de fichiers depuis Sonarr ou Radarr."
    >
      @for (endpoint of endpoints; track endpoint.name) {
        <div class="endpoint">
          <div>
            <h3>{{ endpoint.name }}</h3>
            <code>{{ baseUrl + endpoint.path }}</code>
          </div>
          <button
            matButton="text"
            [attr.aria-label]="'Copier l’adresse ' + endpoint.name"
            (click)="copy(endpoint.path)"
          >
            Copier
          </button>
        </div>
      }
      <mat-expansion-panel
        ><mat-expansion-panel-header
          ><mat-panel-title
            >Configurer l’authentification</mat-panel-title
          ></mat-expansion-panel-header
        >
        <p>
          Utilisez le secret défini dans la configuration du plugin comme mot de passe dans Sonarr
          ou Radarr, ou dans l’en-tête <code>X-Encoding-Webhook-Secret</code>.
        </p></mat-expansion-panel
      >
    </app-configuration-section>
  `,
  styles: `
    .endpoint {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-4);
      flex-wrap: wrap;
      background: var(--surface-container-low);
      padding: var(--space-4);
      margin-bottom: var(--space-2);
      border-radius: var(--radius-md);
    }
    h3 {
      margin: 0 0 var(--space-2);
      font-size: var(--font-body);
    }
    code {
      overflow-wrap: anywhere;
      font-size: var(--font-label);
    }
    mat-expansion-panel {
      background: var(--surface-container-low);
      box-shadow: none;
      margin-top: var(--space-4);
    }
    p {
      color: var(--text-secondary);
      line-height: 1.6;
    }
  `,
})
export class WebhooksTabComponent {
  private readonly clipboard = inject(ClipboardService);
  private readonly notifications = inject(NotificationService);
  protected readonly baseUrl = window.location.origin;
  protected readonly endpoints = [
    { name: 'Sonarr', path: '/api/v1/integrations/sonarr/webhook' },
    { name: 'Radarr', path: '/api/v1/integrations/radarr/webhook' },
  ];
  protected async copy(path: string): Promise<void> {
    this.notifications.show(
      (await this.clipboard.copy(this.baseUrl + path))
        ? 'Adresse copiée.'
        : 'Impossible de copier l’adresse.',
    );
  }
}
