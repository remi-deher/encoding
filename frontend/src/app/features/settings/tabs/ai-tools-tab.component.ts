import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatTooltipModule } from '@angular/material/tooltip';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { AiSettings, AiSettingsUpdateRequest, AiToolInfo } from '../../../shared/models/ai-tools';
import { DeviceAuthDialogComponent } from '../../../shared/ui/device-auth-dialog.component';

@Component({
  selector: 'app-ai-tools-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CommonModule,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatProgressSpinnerModule,
    MatTooltipModule,
  ],
  template: `
    <div class="tab-header">
      <div class="tab-titles">
        <h2>Intelligence Artificielle & Fournisseurs</h2>
        <p>Configuration des moteurs LLM (Ollama, OpenAI) et gestion des comptes CLI connectés.</p>
      </div>
    </div>

    <div class="ai-tools-grid">
      <!-- Section 1 : Configuration des Fournisseurs LLM -->
      <section class="tool-card ai-settings-card">
        <div class="card-header">
          <div class="header-titles">
            <span class="card-eyebrow">Modèles de langage (LLM)</span>
            <h3>Fournisseur actif & Paramètres d'inférence</h3>
          </div>
          @if (savingSettings()) {
            <mat-spinner diameter="24"></mat-spinner>
          }
        </div>

        <p class="tool-description">
          Définissez le fournisseur utilisé pour la génération et le refactoring de flows, la
          traduction automatique de sous-titres et l'enrichissement des métadonnées de médias.
        </p>

        @if (loadingSettings()) {
          <div class="loading-box">
            <mat-spinner diameter="32"></mat-spinner>
            <span>Chargement des réglages IA...</span>
          </div>
        } @else {
          <div class="settings-form">
            <!-- Fournisseur par défaut -->
            <mat-form-field appearance="outline" class="form-field-full">
              <mat-label>Fournisseur IA par défaut</mat-label>
              <mat-select
                [value]="defaultProvider()"
                (selectionChange)="defaultProvider.set($event.value)"
              >
                <mat-option value="ollama">Ollama (Local / Réseau - Recommandé)</mat-option>
                <mat-option value="openai">OpenAI (Cloud / API distante)</mat-option>
              </mat-select>
              <mat-hint
                >Sélectionne le moteur utilisé par les blocs de flow et l'assistant IA</mat-hint
              >
            </mat-form-field>

            <!-- Configuration Ollama -->
            <div class="provider-subsection" [class.is-dimmed]="defaultProvider() !== 'ollama'">
              <div class="subsection-header">
                <mat-icon>dns</mat-icon>
                <h4>Configuration Ollama</h4>
              </div>
              <div class="form-grid">
                <mat-form-field appearance="outline">
                  <mat-label>URL du serveur Ollama</mat-label>
                  <input
                    matInput
                    [value]="ollamaBaseUrl()"
                    (input)="onOllamaBaseUrlChange($event)"
                    placeholder="http://localhost:11434"
                  />
                  <mat-hint
                    >Ex: http://localhost:11434 ou http://host.docker.internal:11434</mat-hint
                  >
                </mat-form-field>

                <mat-form-field appearance="outline">
                  <mat-label>Nom du modèle Ollama</mat-label>
                  <input
                    matInput
                    [value]="ollamaModel()"
                    (input)="onOllamaModelChange($event)"
                    placeholder="llama3:latest"
                  />
                  <mat-hint>Ex: llama3:latest, qwen2.5:7b, mistral:latest</mat-hint>
                </mat-form-field>
              </div>
            </div>

            <!-- Configuration OpenAI -->
            <div class="provider-subsection" [class.is-dimmed]="defaultProvider() !== 'openai'">
              <div class="subsection-header">
                <mat-icon>cloud_queue</mat-icon>
                <h4>Configuration OpenAI / API Compatible</h4>
              </div>
              <div class="form-grid">
                <mat-form-field appearance="outline">
                  <mat-label>URL de base de l'API</mat-label>
                  <input
                    matInput
                    [value]="openAiBaseUrl()"
                    (input)="onOpenAiBaseUrlChange($event)"
                    placeholder="https://api.openai.com/v1"
                  />
                  <mat-hint>Compatible OpenAI, vLLM, LiteLLM, OpenRouter, etc.</mat-hint>
                </mat-form-field>

                <mat-form-field appearance="outline">
                  <mat-label>Nom du modèle</mat-label>
                  <input
                    matInput
                    [value]="openAiModel()"
                    (input)="onOpenAiModelChange($event)"
                    placeholder="gpt-4o-mini"
                  />
                  <mat-hint>Ex: gpt-4o-mini, gpt-4o, claude-3-5-sonnet</mat-hint>
                </mat-form-field>

                <mat-form-field appearance="outline" class="form-field-full">
                  <mat-label>Clé d'API OpenAI</mat-label>
                  <input
                    matInput
                    [type]="showApiKey() ? 'text' : 'password'"
                    [value]="openAiApiKey()"
                    (input)="onOpenAiApiKeyChange($event)"
                    [placeholder]="
                      hasOpenAiApiKey() ? '•••••••••••••••• (Clé existante configurée)' : 'sk-...'
                    "
                  />
                  <button
                    mat-icon-button
                    matSuffix
                    type="button"
                    (click)="showApiKey.set(!showApiKey())"
                    [attr.aria-label]="showApiKey() ? 'Masquer la clé' : 'Afficher la clé'"
                  >
                    <mat-icon>{{ showApiKey() ? 'visibility_off' : 'visibility' }}</mat-icon>
                  </button>
                  <mat-hint>
                    @if (hasOpenAiApiKey()) {
                      <span class="text-success"
                        >Une clé d'API est enregistrée. Laissez vide pour la conserver.</span
                      >
                    } @else {
                      Requis pour effectuer des appels vers OpenAI ou un proxy sécurisé.
                    }
                  </mat-hint>
                </mat-form-field>
              </div>
            </div>

            <!-- Paramètres d'exécution & Timeout -->
            <div class="provider-subsection">
              <div class="subsection-header">
                <mat-icon>tune</mat-icon>
                <h4>Paramètres d'exécution</h4>
              </div>
              <div class="form-grid">
                <mat-form-field appearance="outline">
                  <mat-label>Délai d'expiration (secondes)</mat-label>
                  <input
                    matInput
                    type="number"
                    [value]="timeoutSeconds()"
                    (input)="onTimeoutSecondsChange($event)"
                    min="10"
                    max="600"
                  />
                  <mat-hint>Délai maximal d'un appel HTTP vers le LLM</mat-hint>
                </mat-form-field>

                <mat-form-field appearance="outline">
                  <mat-label>Taille de lot pour les sous-titres</mat-label>
                  <input
                    matInput
                    type="number"
                    [value]="subtitleBatchSize()"
                    (input)="onSubtitleBatchSizeChange($event)"
                    min="5"
                    max="200"
                  />
                  <mat-hint>Nombre de lignes groupées par requête de traduction</mat-hint>
                </mat-form-field>
              </div>
            </div>

            <div class="card-actions">
              <button
                mat-flat-button
                color="primary"
                type="button"
                [disabled]="savingSettings()"
                (click)="saveSettings()"
              >
                <mat-icon>save</mat-icon> Enregistrer les paramètres IA
              </button>
            </div>
          </div>
        }
      </section>

      <!-- Section 2 : CLIs & Outils de Ligne de Commande -->
      <div class="section-title-row">
        <h3>Comptes CLI & Authentification Headless</h3>
        <p>Connexion par Device Code Flow RFC 8628 (sans saisie directe de clé API sur l'hôte).</p>
      </div>

      @if (loadingTools()) {
        <div class="loading-box">
          <mat-spinner diameter="32"></mat-spinner>
          <span>Chargement des outils CLI...</span>
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
      .section-title-row {
        margin-top: 16px;
      }
      .section-title-row h3 {
        margin: 0 0 4px;
        font-size: 1.15rem;
        font-weight: 600;
      }
      .section-title-row p {
        margin: 0;
        color: var(--mat-sys-on-surface-variant);
        font-size: 0.88rem;
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
      .ai-settings-card {
        border-color: var(--mat-sys-primary, #64b5f6);
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
      .settings-form {
        display: flex;
        flex-direction: column;
        gap: 16px;
        margin-top: 8px;
      }
      .provider-subsection {
        background: var(--mat-sys-surface-container-high, #222);
        border-radius: 8px;
        padding: 16px;
        display: flex;
        flex-direction: column;
        gap: 12px;
        transition: opacity 0.2s ease;
      }
      .provider-subsection.is-dimmed {
        opacity: 0.7;
      }
      .subsection-header {
        display: flex;
        align-items: center;
        gap: 8px;
        color: var(--mat-sys-primary, #64b5f6);
      }
      .subsection-header h4 {
        margin: 0;
        font-size: 0.95rem;
        font-weight: 600;
      }
      .form-grid {
        display: grid;
        grid-template-columns: repeat(auto-fit, minmax(280px, 1fr));
        gap: 16px;
      }
      .form-field-full {
        width: 100%;
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
  readonly loadingTools = signal(true);

  // Settings signals
  readonly loadingSettings = signal(true);
  readonly savingSettings = signal(false);
  readonly defaultProvider = signal<string>('ollama');
  readonly ollamaBaseUrl = signal<string>('http://localhost:11434');
  readonly ollamaModel = signal<string>('llama3:latest');
  readonly openAiBaseUrl = signal<string>('https://api.openai.com/v1');
  readonly openAiModel = signal<string>('gpt-4o-mini');
  readonly openAiApiKey = signal<string>('');
  readonly hasOpenAiApiKey = signal<boolean>(false);
  readonly showApiKey = signal<boolean>(false);
  readonly timeoutSeconds = signal<number>(120);
  readonly subtitleBatchSize = signal<number>(50);

  ngOnInit(): void {
    this.refreshTools();
    this.refreshSettings();
  }

  refreshTools(): void {
    this.loadingTools.set(true);
    this.aiService.listTools().subscribe({
      next: (list) => {
        this.tools.set(list);
        this.loadingTools.set(false);
      },
      error: () => this.loadingTools.set(false),
    });
  }

  refreshSettings(): void {
    this.loadingSettings.set(true);
    this.aiService.getSettings().subscribe({
      next: (settings) => {
        this.applySettings(settings);
        this.loadingSettings.set(false);
      },
      error: () => {
        this.loadingSettings.set(false);
      },
    });
  }

  private applySettings(settings: AiSettings): void {
    this.defaultProvider.set(settings.defaultProvider || 'ollama');
    this.ollamaBaseUrl.set(settings.ollamaBaseUrl || 'http://localhost:11434');
    this.ollamaModel.set(settings.ollamaModel || 'llama3:latest');
    this.openAiBaseUrl.set(settings.openAiBaseUrl || 'https://api.openai.com/v1');
    this.openAiModel.set(settings.openAiModel || 'gpt-4o-mini');
    this.hasOpenAiApiKey.set(!!settings.hasOpenAiApiKey);
    this.timeoutSeconds.set(settings.timeoutSeconds || 120);
    this.subtitleBatchSize.set(settings.subtitleBatchSize || 50);
  }

  onOllamaBaseUrlChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.ollamaBaseUrl.set(input.value);
  }

  onOllamaModelChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.ollamaModel.set(input.value);
  }

  onOpenAiBaseUrlChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.openAiBaseUrl.set(input.value);
  }

  onOpenAiModelChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.openAiModel.set(input.value);
  }

  onOpenAiApiKeyChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.openAiApiKey.set(input.value);
  }

  onTimeoutSecondsChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const val = parseInt(input.value, 10);
    if (!isNaN(val)) {
      this.timeoutSeconds.set(val);
    }
  }

  onSubtitleBatchSizeChange(event: Event): void {
    const input = event.target as HTMLInputElement;
    const val = parseInt(input.value, 10);
    if (!isNaN(val)) {
      this.subtitleBatchSize.set(val);
    }
  }

  saveSettings(): void {
    this.savingSettings.set(true);
    const req: AiSettingsUpdateRequest = {
      defaultProvider: this.defaultProvider(),
      ollamaBaseUrl: this.ollamaBaseUrl().trim(),
      ollamaModel: this.ollamaModel().trim(),
      openAiBaseUrl: this.openAiBaseUrl().trim(),
      openAiModel: this.openAiModel().trim(),
      openAiApiKey: this.openAiApiKey().trim() || null,
      timeoutSeconds: this.timeoutSeconds(),
      subtitleBatchSize: this.subtitleBatchSize(),
    };

    this.aiService.updateSettings(req).subscribe({
      next: (res) => {
        this.applySettings(res);
        this.savingSettings.set(false);
        this.openAiApiKey.set('');
        this.notifications.show('Configuration IA enregistrée avec succès.');
      },
      error: () => {
        this.savingSettings.set(false);
        this.notifications.show('Erreur lors de l’enregistrement de la configuration IA.');
      },
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
