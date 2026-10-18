import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { NotificationService } from '../services/notification.service';
import { Subscription } from 'rxjs';
import {
  MAT_DIALOG_DATA,
  MatDialogActions,
  MatDialogContent,
  MatDialogRef,
  MatDialogTitle,
} from '@angular/material/dialog';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AiToolsService } from '../services/ai-tools.service';
import { ClipboardService } from '../services/clipboard.service';
import { DeviceAuthPrompt } from '../models/ai-tools';

export interface DeviceAuthDialogData {
  readonly toolId: string;
  readonly toolName: string;
  readonly prompt: DeviceAuthPrompt;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-device-auth-dialog',
  imports: [
    CommonModule,
    MatButtonModule,
    MatDialogActions,
    MatDialogContent,
    MatDialogTitle,
    MatIconModule,
    MatProgressSpinnerModule,
  ],
  template: `
    <section class="app-dialog device-auth-dialog">
      <header class="app-dialog-header">
        <div class="app-dialog-heading">
          <p class="app-dialog-eyebrow">Authentification sécurisée (Device Code)</p>
          <h2 mat-dialog-title>Associer {{ data.toolName }}</h2>
        </div>
      </header>

      <mat-dialog-content class="app-dialog-body device-auth-body">
        <p class="instruction">
          Pour connecter votre compte sans clé API, ouvrez le lien officiel ci-dessous dans votre
          navigateur et saisissez votre code unique d'autorisation :
        </p>

        <div class="code-box">
          <span class="code-value">{{ data.prompt.userCode }}</span>
          <button matButton="outlined" type="button" (click)="copyCode()">
            <mat-icon>{{ copied() ? 'check' : 'content_copy' }}</mat-icon>
            {{ copied() ? 'Copié !' : 'Copier le code' }}
          </button>
        </div>

        <div class="action-link-box">
          <a
            matButton="filled"
            [href]="data.prompt.verificationUri"
            target="_blank"
            rel="noopener noreferrer"
          >
            <mat-icon>open_in_new</mat-icon>
            Ouvrir {{ data.prompt.verificationUri }}
          </a>
        </div>

        <div class="status-indicator">
          @if (isChecking()) {
            <mat-spinner diameter="20"></mat-spinner>
            <span>Vérification de l'autorisation...</span>
          } @else if (isAuthorized()) {
            <span class="authorized-badge">
              <mat-icon>check_circle</mat-icon> Compte associé avec succès !
            </span>
          } @else {
            <button
              matButton="text"
              type="button"
              [disabled]="isChecking()"
              (click)="checkStatus()"
            >
              <mat-icon>refresh</mat-icon> J'ai validé le code
            </button>
          }
        </div>
        @if (statusMessage()) {
          <p role="status">{{ statusMessage() }}</p>
        }
      </mat-dialog-content>

      <mat-dialog-actions align="end">
        <button matButton="text" type="button" (click)="close()">Fermer</button>
      </mat-dialog-actions>
    </section>
  `,
  styles: [
    `
      .device-auth-dialog {
        min-width: 0;
        width: 100%;
        max-width: 520px;
      }
      .device-auth-body {
        display: flex;
        flex-direction: column;
        gap: 16px;
        padding: 16px 0;
      }
      .instruction {
        margin: 0;
        color: var(--mat-sys-on-surface-variant);
        font-size: 0.95rem;
        line-height: 1.4;
      }
      .code-box {
        display: flex;
        align-items: center;
        justify-content: space-between;
        padding: 12px 16px;
        background: var(--surface-container-high);
        border-radius: var(--radius-md);
        flex-wrap: wrap;
        gap: var(--space-3);
      }
      .code-value {
        font-family: monospace;
        font-size: 1.4rem;
        font-weight: 700;
        letter-spacing: 2px;
        color: var(--mat-sys-primary);
      }
      .action-link-box {
        display: flex;
        justify-content: center;
        margin: 8px 0;
      }
      .status-indicator {
        display: flex;
        align-items: center;
        justify-content: center;
        gap: 8px;
        min-height: 36px;
      }
      .authorized-badge {
        display: flex;
        align-items: center;
        gap: 6px;
        color: var(--success);
        font-weight: 600;
      }
    `,
  ],
})
export class DeviceAuthDialogComponent implements OnInit, OnDestroy {
  readonly data = inject<DeviceAuthDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<DeviceAuthDialogComponent>);
  private readonly aiService = inject(AiToolsService);
  private readonly clipboard = inject(ClipboardService);

  private readonly notifications = inject(NotificationService);
  private polling?: ReturnType<typeof setInterval>;
  private subscription?: Subscription;
  private expiresAt = 0;
  readonly expired = signal(false);
  readonly statusMessage = signal('');
  ngOnInit(): void {
    this.expiresAt = Date.now() + this.data.prompt.expiresInSeconds * 1000;
    this.polling = setInterval(
      () => this.checkStatus(),
      Math.max(2, this.data.prompt.intervalSeconds) * 1000,
    );
    this.checkStatus();
  }
  ngOnDestroy(): void {
    clearInterval(this.polling);
    this.subscription?.unsubscribe();
    if (!this.isAuthorized())
      this.aiService.cancelAuth(this.data.toolId).subscribe({
        error: () =>
          this.notifications.show(
            'La tentative expirera automatiquement si son annulation n’a pas pu être confirmée.',
          ),
      });
  }
  readonly copied = signal(false);
  readonly isChecking = signal(false);
  readonly isAuthorized = signal(false);

  copyCode(): void {
    this.clipboard.copy(this.data.prompt.userCode);
    this.copied.set(true);
    setTimeout(() => this.copied.set(false), 3000);
  }

  checkStatus(): void {
    if (this.isChecking() || this.isAuthorized() || this.expired()) return;
    if (Date.now() >= this.expiresAt) {
      this.expired.set(true);
      this.statusMessage.set('Le code a expiré. Fermez puis relancez l’association.');
      clearInterval(this.polling);
      return;
    }
    this.isChecking.set(true);
    this.subscription = this.aiService.getDeviceAuthStatus(this.data.toolId).subscribe({
      next: (status) => {
        this.isChecking.set(false);
        if (status.state === 'authorized') {
          this.isAuthorized.set(true);
          clearInterval(this.polling);
          this.notifications.show('Compte associé sur ce nœud.');
          this.dialogRef.close({ success: true });
        } else if (status.state === 'failed' || status.state === 'expired') {
          this.expired.set(true);
          this.statusMessage.set(
            status.errorMessage || 'La connexion a échoué ou a expiré. Relancez l’association.',
          );
          clearInterval(this.polling);
        }
      },
      error: () => {
        this.isChecking.set(false);
        this.statusMessage.set('Vérification indisponible. Nouvelle tentative automatique.');
      },
    });
  }

  close(): void {
    this.dialogRef.close({ success: this.isAuthorized() });
  }
}
