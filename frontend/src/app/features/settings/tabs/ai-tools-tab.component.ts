import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { AiToolInfo } from '../../../shared/models/ai-tools';
import { DeviceAuthDialogComponent } from '../../../shared/ui/device-auth-dialog.component';

@Component({
  selector: 'app-ai-tools-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule, MatButtonModule, MatIconModule, MatProgressSpinnerModule],
  template: `
    <div class="tab-header">
      <div class="tab-titles">
        <h2>Outils d'Intelligence Artificielle & CLIs</h2>
        <p>Connexion headless par compte (Device Code Flow RFC 8628) sans clé d'API requise.</p>
      </div>
    </div>

    <div class="ai-tools-grid">
      @if (loading()) {
        <div class="loading-box">
          <mat-spinner diameter="32"></mat-spinner>
          <span>Chargement des outils IA...</span>
        </div>
      } @else {
        @for (tool of tools(); track tool.id) {
          <section class="tool-card">
            <div class="card-header">
              <div class="header-titles">
                <span class="card-eyebrow">{{ tool.category }}</span>
                <h3>{{ tool.name }}</h3>
              </div>
              <div class="header-badges">
                <span
                  class="status-pill"
                  [class.status-pill_success]="tool.authStatus.state === 'authorized'"
                  [class.status-pill_warning]="tool.authStatus.state === 'pending'"
                  [class.status-pill_muted]="tool.authStatus.state === 'unconfigured'"
                >
                  {{ authLabel(tool.authStatus.state) }}
                </span>
              </div>
            </div>

            <p class="tool-description">{{ tool.description }}</p>

            <div class="card-meta">
              <span class="meta-item">
                <strong>Identifiant :</strong> <code>{{ tool.id }}</code>
              </span>
              @if (tool.authStatus.accountName) {
                <span class="meta-item">
                  <strong>Compte :</strong> {{ tool.authStatus.accountName }}
                </span>
              }
              <span class="meta-item">
                <strong>Binaire système :</strong>
                <span
                  [class.text-success]="tool.isInstalled"
                  [class.text-muted]="!tool.isInstalled"
                >
                  {{ tool.isInstalled ? 'Détecté sur l’hôte' : 'Simulé / Non installé' }}
                </span>
              </span>
            </div>

            <div class="card-actions">
              @if (tool.authStatus.state === 'authorized') {
                <button mat-stroked-button color="warn" type="button" (click)="revoke(tool.id)">
                  <mat-icon>link_off</mat-icon> Déconnecter
                </button>
              } @else {
                <button mat-flat-button color="primary" type="button" (click)="startAuth(tool)">
                  <mat-icon>link</mat-icon> Associer mon compte
                </button>
              }
            </div>
          </section>
        }
      }

      <div class="info-banner">
        <mat-icon>verified_user</mat-icon>
        <div class="info-text">
          <strong>Règle d'isolation matérielle :</strong>
          Ces outils exécutent des modèles de langage et de traduction sous CPU/Réseau. Ils ne
          peuvent en aucun cas remplacer les encodeurs matériels du conteneur (NVENC, QSV, VA-API).
        </div>
      </div>
    </div>
  `,
  styles: [
    `
      .tab-header {
        margin-bottom: 24px;
      }
      .tab-titles h2 {
        margin: 0 0 4px;
        font-size: 1.3rem;
        font-weight: 700;
      }
      .tab-titles p {
        margin: 0;
        color: var(--mat-sys-on-surface-variant);
        font-size: 0.9rem;
      }
      .ai-tools-grid {
        display: flex;
        flex-direction: column;
        gap: 16px;
      }
      .loading-box {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 24px;
      }
      .tool-card {
        background: var(--mat-sys-surface-container, #1a1a1a);
        border: 1px solid var(--mat-sys-outline-variant, #333);
        border-radius: 12px;
        padding: 20px;
        display: flex;
        flex-direction: column;
        gap: 12px;
      }
      .card-header {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
      }
      .card-eyebrow {
        text-transform: uppercase;
        font-size: 0.75rem;
        letter-spacing: 1px;
        color: var(--mat-sys-primary, #64b5f6);
      }
      .header-titles h3 {
        margin: 2px 0 0;
        font-size: 1.15rem;
        font-weight: 600;
      }
      .status-pill {
        display: inline-block;
        padding: 4px 10px;
        border-radius: 12px;
        font-size: 0.8rem;
        font-weight: 600;
      }
      .status-pill_success {
        background: rgba(76, 175, 80, 0.2);
        color: #81c784;
      }
      .status-pill_warning {
        background: rgba(255, 152, 0, 0.2);
        color: #ffb74d;
      }
      .status-pill_muted {
        background: rgba(158, 158, 158, 0.15);
        color: #9e9e9e;
      }
      .tool-description {
        margin: 0;
        color: var(--mat-sys-on-surface-variant);
        font-size: 0.9rem;
        line-height: 1.4;
      }
      .card-meta {
        display: flex;
        flex-wrap: wrap;
        gap: 16px;
        font-size: 0.85rem;
        background: var(--mat-sys-surface-container-high, #222);
        padding: 10px 14px;
        border-radius: 8px;
      }
      .text-success {
        color: #81c784;
      }
      .text-muted {
        color: #9e9e9e;
      }
      .card-actions {
        display: flex;
        justify-content: flex-end;
        margin-top: 4px;
      }
      .info-banner {
        display: flex;
        align-items: flex-start;
        gap: 12px;
        padding: 16px;
        background: var(--mat-sys-surface-container-low, #141414);
        border-left: 4px solid var(--mat-sys-primary, #64b5f6);
        border-radius: 6px;
        color: var(--mat-sys-on-surface-variant);
        font-size: 0.88rem;
        line-height: 1.4;
      }
    `,
  ],
})
export class AiToolsTabComponent implements OnInit {
  private readonly aiService = inject(AiToolsService);
  private readonly notifications = inject(NotificationService);
  private readonly dialog = inject(MatDialog);

  readonly tools = signal<readonly AiToolInfo[]>([]);
  readonly loading = signal(true);

  ngOnInit(): void {
    this.refreshTools();
  }

  refreshTools(): void {
    this.loading.set(true);
    this.aiService.listTools().subscribe({
      next: (list) => {
        this.tools.set(list);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  startAuth(tool: AiToolInfo): void {
    this.aiService.startDeviceAuth(tool.id).subscribe({
      next: (res) => {
        const dialogRef = this.dialog.open(DeviceAuthDialogComponent, {
          data: {
            toolId: tool.id,
            toolName: tool.name,
            prompt: res.prompt,
          },
        });
        dialogRef.afterClosed().subscribe((result: { success?: boolean } | undefined) => {
          if (result?.success) {
            this.notifications.show(`Compte ${tool.name} associé avec succès !`);
          }
          this.refreshTools();
        });
      },
      error: () => this.notifications.show('Impossible de démarrer l’authentification.'),
    });
  }

  revoke(toolId: string): void {
    this.aiService.revokeAuth(toolId).subscribe({
      next: () => {
        this.notifications.show('Compte déconnecté.');
        this.refreshTools();
      },
      error: () => this.notifications.show('Erreur lors de la déconnexion.'),
    });
  }

  authLabel(state: string): string {
    switch (state) {
      case 'authorized':
        return 'Connecté';
      case 'pending':
        return 'En attente de validation';
      case 'expired':
        return 'Expiré';
      case 'failed':
        return 'Échec';
      default:
        return 'Non configuré';
    }
  }
}
