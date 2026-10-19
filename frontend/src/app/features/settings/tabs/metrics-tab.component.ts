import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { NotificationService } from '../../../shared/services/notification.service';
@Component({
  selector: 'app-metrics-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ConfigurationSectionComponent, MatButtonModule],
  template: `<app-configuration-section
    title="Métriques Prometheus"
    description="Point de collecte pour votre outil de supervision."
    ><div class="endpoint">
      <code>{{ address }}</code
      ><button matButton="text" (click)="copy()">Copier l’adresse</button>
    </div></app-configuration-section
  >`,
  styles: `
    .endpoint {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-4);
      flex-wrap: wrap;
      padding: var(--space-4);
      border-radius: var(--radius-md);
      background: var(--surface-container-low);
    }
    code {
      overflow-wrap: anywhere;
      font-size: var(--font-label);
    }
  `,
})
export class MetricsTabComponent {
  private readonly clipboard = inject(ClipboardService);
  private readonly notifications = inject(NotificationService);
  protected readonly address = `${window.location.origin}/metrics`;
  protected async copy(): Promise<void> {
    this.notifications.show(
      (await this.clipboard.copy(this.address))
        ? 'Adresse copiée.'
        : 'Impossible de copier l’adresse.',
    );
  }
}
