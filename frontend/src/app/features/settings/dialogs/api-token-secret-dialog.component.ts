import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { NotificationService } from '../../../shared/services/notification.service';

export interface ApiTokenSecretDialogData {
  readonly name: string;
  readonly secret: string;
}

@Component({
  selector: 'app-api-token-secret-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatDialogActions, MatDialogContent, MatDialogTitle],
  template: `
    <section class="app-dialog">
      <h2 mat-dialog-title class="app-dialog-header">Jeton d’API généré</h2>

      <mat-dialog-content class="app-dialog-body secret-dialog-content">
        <p class="warning-text">
          ⚠️ <strong>Copiez ce jeton dès maintenant.</strong> Pour des raisons de sécurité, il ne sera plus jamais réaffiché.
        </p>

        <div class="secret-field">
          <code class="secret-code">{{ data.secret }}</code>
          <button matButton="tonal" type="button" (click)="copy()">
            Copier
          </button>
        </div>

        @if (data.secret) {
          <p class="sub-hint">
            Utilisation : ajoutez l’en-tête HTTP <code>Authorization: Bearer {{ data.secret.slice(0, 16) }}…</code>
          </p>
        }
      </mat-dialog-content>

      <mat-dialog-actions class="app-dialog-footer" align="end">
        <button matButton="filled" type="button" (click)="close()">
          J’ai bien copié mon jeton
        </button>
      </mat-dialog-actions>
    </section>
  `,
  styles: `
    .secret-dialog-content {
      display: grid;
      gap: var(--space-3);
      min-width: 420px;
      padding-top: var(--space-2);
    }

    .warning-text {
      margin: 0;
      font-size: var(--font-body-sm);
      color: var(--warning);
      background: var(--warning-soft);
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-md);
      line-height: 1.4;
    }

    .secret-field {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container-highest);
      border: 1px solid var(--outline-variant);
    }

    .secret-code {
      flex: 1;
      font-family: ui-monospace, 'JetBrains Mono', 'Consolas', monospace;
      font-size: var(--font-body-sm);
      color: var(--primary);
      user-select: all;
      word-break: break-all;
    }

    .sub-hint {
      margin: 0;
      font-size: var(--font-label);
      color: var(--text-secondary);
    }

    .sub-hint code {
      font-family: ui-monospace, monospace;
      color: var(--text-primary);
    }
  `,
})
export class ApiTokenSecretDialogComponent {
  protected readonly data = inject<ApiTokenSecretDialogData>(MAT_DIALOG_DATA);
  protected readonly dialogRef = inject(MatDialogRef<ApiTokenSecretDialogComponent>);
  private readonly notifications = inject(NotificationService);
  private readonly clipboardService = inject(ClipboardService);

  protected async copy(): Promise<void> {
    const text = this.data?.secret;
    if (!text) {
      this.notifications.show('Aucun jeton à copier.');
      return;
    }

    const success = await this.clipboardService.copy(text);
    if (success) {
      this.notifications.show('Jeton copié dans le presse-papier.');
    } else {
      this.notifications.show('Erreur lors de la copie du jeton.');
    }
  }

  protected close(): void {
    this.dialogRef.close();
  }
}
