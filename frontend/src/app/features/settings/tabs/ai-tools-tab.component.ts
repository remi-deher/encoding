import { CategoryTabsComponent } from '../../../shared/ui/category-tabs.component';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatDialog } from '@angular/material/dialog';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatMenuModule } from '@angular/material/menu';
import { AiToolsService } from '../../../shared/services/ai-tools.service';
import { NotificationService } from '../../../shared/services/notification.service';
import {
  AiSettings,
  AiSettingsUpdateRequest,
  AiToolInfo,
  AiClusterView,
} from '../../../shared/models/ai-tools';
import { DeviceAuthDialogComponent } from '../../../shared/ui/device-auth-dialog.component';
import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'app-ai-tools-tab',
  host: { '(window:beforeunload)': 'beforeUnload($event)' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    CategoryTabsComponent,
    FormsModule,
    MatButtonModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatSelectModule,
    MatProgressSpinnerModule,
    MatExpansionModule,
    MatMenuModule,
  ],
  template: `
    <header class="page-heading">
      <div>
        <h2>IA & fournisseurs</h2>
        <p>Choisissez le moteur du cluster et gérez ses connexions.</p>
      </div>
      <button matButton="text" type="button" [disabled]="loadingTools()" (click)="refreshTools()">
        Actualiser les connexions
      </button>
    </header>
    @if (loadingSettings()) {
      <mat-spinner diameter="28" aria-label="Chargement des paramètres" />
    } @else {
      <form #settingsForm="ngForm" (ngSubmit)="saveSettings()">
        <section class="default-provider">
          <div>
            <h3>Moteur par défaut</h3>
            <p>Utilisé quand une demande ne précise pas de fournisseur.</p>
          </div>
          <mat-form-field appearance="outline"
            ><mat-label>Moteur du cluster</mat-label>
            <mat-select
              name="defaultProvider"
              [(ngModel)]="defaultProvider"
              [disabled]="!isAdmin()"
            >
              @for (provider of providers; track provider.id) {
                <mat-option [value]="provider.id">{{ provider.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        </section>
        <div class="workspace">
          <app-category-tabs
            label="Fournisseurs IA"
            [options]="providerTabs()"
            [selected]="selectedProvider()"
            (selectedChange)="selectedProvider.set($event)"
          />
          <section class="provider-sheet" aria-labelledby="provider-title">
            <header class="sheet-heading">
              <div>
                <h3 id="provider-title">{{ selectedName() }}</h3>
                <p class="help">{{ clusterLabel(selectedProvider()) }}</p>
                <p>
                  Configuration de la connexion sur ce nœud. Les demandes du cluster peuvent
                  l’utiliser.
                </p>
              </div>
              @if (selectedTool(); as tool) {
                @if (tool.authStatus.state === 'authorized' && isAdmin()) {
                  <button
                    matIconButton
                    type="button"
                    [matMenuTriggerFor]="connectionMenu"
                    aria-label="Actions de connexion"
                  >
                    <mat-icon>more_vert</mat-icon>
                  </button>
                  <mat-menu #connectionMenu="matMenu"
                    ><button mat-menu-item type="button" (click)="revoke(tool.id)">
                      <mat-icon>link_off</mat-icon>Déconnecter ce nœud
                    </button></mat-menu
                  >
                }
              }
            </header>
            @switch (selectedProvider()) {
              @case ('ollama') {
                <div class="fields">
                  <mat-form-field appearance="outline"
                    ><mat-label>Adresse du serveur</mat-label
                    ><input
                      matInput
                      name="ollamaBaseUrl"
                      [(ngModel)]="ollamaBaseUrl"
                      [readonly]="!isAdmin()"
                      required
                      type="url"
                  /></mat-form-field>
                  <mat-form-field appearance="outline"
                    ><mat-label>Modèle</mat-label
                    ><input
                      matInput
                      name="ollamaModel"
                      [(ngModel)]="ollamaModel"
                      [readonly]="!isAdmin()"
                      required
                  /></mat-form-field>
                </div>
              }
              @case ('openai') {
                <div class="fields">
                  <mat-form-field appearance="outline"
                    ><mat-label>Adresse de l’API compatible OpenAI</mat-label
                    ><input
                      matInput
                      name="openAiBaseUrl"
                      [(ngModel)]="openAiBaseUrl"
                      [readonly]="!isAdmin()"
                      required
                      type="url"
                  /></mat-form-field>
                  <mat-form-field appearance="outline"
                    ><mat-label>Modèle</mat-label
                    ><input
                      matInput
                      name="openAiModel"
                      [(ngModel)]="openAiModel"
                      [readonly]="!isAdmin()"
                      required
                  /></mat-form-field>
                  <mat-form-field appearance="outline"
                    ><mat-label>Clé API</mat-label
                    ><input
                      matInput
                      name="openAiApiKey"
                      type="password"
                      autocomplete="new-password"
                      [(ngModel)]="openAiApiKey"
                      [readonly]="!isAdmin()"
                    />
                    <mat-hint>{{
                      hasOpenAiApiKey()
                        ? 'Clé enregistrée. Laissez vide pour la conserver.'
                        : 'La clé reste sur ce nœud.'
                    }}</mat-hint>
                  </mat-form-field>
                </div>
              }
              @default {
                @if (selectedTool(); as tool) {
                  <div class="connection-status">
                    <span class="badge">{{ authLabel(tool.authStatus.state) }}</span
                    ><span>{{
                      tool.isInstalled
                        ? 'Outil installé sur ce nœud'
                        : 'Outil non installé sur ce nœud'
                    }}</span>
                  </div>
                  @if (tool.authStatus.errorMessage) {
                    <p role="status">{{ tool.authStatus.errorMessage }}</p>
                  }
                  @if (!tool.isInstalled) {
                    <p>Installez l’outil officiel sur ce nœud pour y ajouter une connexion.</p>
                  } @else {
                    @if (isAdmin()) {
                      <button
                        matButton="tonal"
                        type="button"
                        [disabled]="connecting()"
                        (click)="startAuth(tool)"
                      >
                        {{ connecting() ? 'Préparation de la connexion…' : 'Se connecter' }}
                      </button>
                    }
                    <p class="help">
                      @if (tool.id === 'codex-cli') {
                        Le lien et le code sont fournis par Codex. Aucun identifiant n’est partagé
                        entre les nœuds.
                      } @else {
                        La commande Claude Code ouvre une connexion navigateur. Le lien est affiché
                        ici; si la CLI demande un code, vous pourrez le coller pour ce nœud.
                      }
                    </p>
                  }
                } @else {
                  <p>État local indisponible. Actualisez les connexions.</p>
                }
              }
            }
          </section>
        </div>
        <mat-expansion-panel class="advanced">
          <mat-expansion-panel-header
            ><mat-panel-title
              >Paramètres avancés du cluster</mat-panel-title
            ></mat-expansion-panel-header
          >
          <div class="fields">
            <mat-form-field appearance="outline"
              ><mat-label>Délai maximal par tentative (secondes)</mat-label
              ><input
                matInput
                name="timeoutSeconds"
                type="number"
                min="10"
                max="600"
                required
                [(ngModel)]="timeoutSeconds"
                [readonly]="!isAdmin()"
            /></mat-form-field>
            <mat-form-field appearance="outline"
              ><mat-label>Lignes de sous-titres par lot</mat-label
              ><input
                matInput
                name="subtitleBatchSize"
                type="number"
                min="5"
                max="200"
                required
                [(ngModel)]="subtitleBatchSize"
                [readonly]="!isAdmin()"
            /></mat-form-field>
          </div>
        </mat-expansion-panel>
        @if (isAdmin()) {
          <footer>
            <span
              >Le moteur et les limites sont partagés. Les adresses, modèles et secrets restent
              propres à ce nœud.</span
            ><button
              matButton="filled"
              type="submit"
              [disabled]="savingSettings() || settingsForm.invalid"
            >
              {{ savingSettings() ? 'Enregistrement…' : 'Enregistrer les paramètres' }}
            </button>
          </footer>
        }
      </form>
    }
    @if (cluster(); as view) {
      <section class="cluster-connections">
        <h3>Connexions disponibles dans le cluster</h3>
        <p>
          Les nœuds répondants déclarent leurs connexions. Une déclaration de connexion ne garantit
          pas le quota ou la disponibilité du modèle.
        </p>
        @for (node of view.nodes; track node.nodeId) {
          <p>
            <strong>{{ node.name }}</strong> ·
            {{
              node.providers.length ? providerNames(node.providers) : 'Aucun fournisseur configuré'
            }}
          </p>
        }
      </section>
    }
  `,
  styleUrl: './ai-tools-tab.component.scss',
})
export class AiToolsTabComponent implements OnInit {
  private readonly aiService = inject(AiToolsService);
  private readonly notifications = inject(NotificationService);
  private readonly dialog = inject(MatDialog);
  readonly isAdmin = inject(AuthService).isAdmin;
  readonly providers = [
    { id: 'ollama', name: 'Ollama' },
    { id: 'openai', name: 'OpenAI / API compatible' },
    { id: 'codex-cli', name: 'Codex CLI' },
    { id: 'claude-cli', name: 'Claude Code' },
  ];
  readonly tools = signal<readonly AiToolInfo[]>([]);
  readonly cluster = signal<AiClusterView | null>(null);
  readonly loadingTools = signal(false);
  readonly loadingSettings = signal(true);
  readonly savingSettings = signal(false);
  readonly connecting = signal(false);
  readonly selectedProvider = signal('ollama');
  readonly selectedName = computed(
    () => this.providers.find((p) => p.id === this.selectedProvider())?.name ?? 'Fournisseur',
  );
  readonly selectedTool = computed(() =>
    this.tools().find((t) => t.id === this.selectedProvider()),
  );
  readonly defaultProvider = signal('ollama');
  readonly providerTabs = computed(() =>
    this.providers.map((provider) => ({
      value: provider.id,
      label: provider.name + (this.defaultProvider() === provider.id ? ' · par défaut' : ''),
    })),
  );
  readonly ollamaBaseUrl = signal('');
  readonly ollamaModel = signal('');
  readonly openAiBaseUrl = signal('');
  readonly openAiModel = signal('');
  readonly openAiApiKey = signal('');
  readonly hasOpenAiApiKey = signal(false);
  readonly timeoutSeconds = signal(120);
  readonly subtitleBatchSize = signal(80);

  private savedFingerprint = '';
  private fingerprint(): string {
    return JSON.stringify([
      this.defaultProvider(),
      this.ollamaBaseUrl(),
      this.ollamaModel(),
      this.openAiBaseUrl(),
      this.openAiModel(),
      this.openAiApiKey(),
      this.timeoutSeconds(),
      this.subtitleBatchSize(),
    ]);
  }
  hasUnsavedChanges(): boolean {
    return (
      !this.loadingSettings() &&
      this.fingerprint() !== this.savedFingerprint
    );
  }
  beforeUnload(event: BeforeUnloadEvent): void {
    if (this.hasUnsavedChanges()) event.preventDefault();
  }
  ngOnInit(): void {
    this.refreshTools();
    this.aiService.getSettings().subscribe({
      next: (settings) => {
        this.applySettings(settings);
        this.selectedProvider.set(settings.defaultProvider);
        this.loadingSettings.set(false);
      },
      error: () => {
        this.notifications.show('Impossible de charger les paramètres IA.');
      },
    });
  }
  refreshTools(): void {
    this.loadingTools.set(true);
    this.aiService.listTools().subscribe({
      next: (tools) => {
        this.tools.set(tools);
        this.loadingTools.set(false);
      },
      error: () => {
        this.loadingTools.set(false);
        this.notifications.show('Impossible de charger les connexions locales.');
      },
    });
    this.aiService.getCluster().subscribe({
      next: (view) => this.cluster.set(view),
      error: () => {
        this.cluster.set(null);
      },
    });
  }
  private applySettings(settings: AiSettings): void {
    this.defaultProvider.set(settings.defaultProvider);
    this.ollamaBaseUrl.set(settings.ollamaBaseUrl);
    this.ollamaModel.set(settings.ollamaModel);
    this.openAiBaseUrl.set(settings.openAiBaseUrl);
    this.openAiModel.set(settings.openAiModel);
    this.hasOpenAiApiKey.set(settings.hasOpenAiApiKey ?? false);
    this.timeoutSeconds.set(settings.timeoutSeconds);
    this.subtitleBatchSize.set(settings.subtitleBatchSize);
    this.savedFingerprint = this.fingerprint();
  }
  saveSettings(): void {
    if (!this.isAdmin()) return;
    this.savingSettings.set(true);
    const request: AiSettingsUpdateRequest = {
      defaultProvider: this.defaultProvider(),
      ollamaBaseUrl: this.ollamaBaseUrl().trim(),
      ollamaModel: this.ollamaModel().trim(),
      openAiBaseUrl: this.openAiBaseUrl().trim(),
      openAiModel: this.openAiModel().trim(),
      openAiApiKey: this.openAiApiKey().trim() || null,
      timeoutSeconds: this.timeoutSeconds(),
      subtitleBatchSize: this.subtitleBatchSize(),
    };
    this.aiService.updateSettings(request).subscribe({
      next: (settings) => {
        this.applySettings(settings);
        this.openAiApiKey.set('');
        this.savedFingerprint = this.fingerprint();
        this.savingSettings.set(false);
        this.notifications.show('Configuration IA enregistrée avec succès.');
        this.refreshTools();
      },
      error: () => {
        this.savingSettings.set(false);
        this.notifications.show('Impossible d’enregistrer les paramètres IA.');
      },
    });
  }
  startAuth(tool: AiToolInfo): void {
    if (!this.isAdmin() || !tool.isInstalled) return;
    this.connecting.set(true);
    this.aiService.startDeviceAuth(tool.id).subscribe({
      next: (response) => {
        this.connecting.set(false);
        const ref = this.dialog.open(DeviceAuthDialogComponent, {
          data: { toolId: tool.id, toolName: tool.name, prompt: response.prompt },
          width: '520px',
          maxWidth: 'calc(100vw - 32px)',
        });
        ref.afterClosed().subscribe(() => this.refreshTools());
      },
      error: (error: HttpErrorResponse) => {
        this.connecting.set(false);
        this.notifications.show(this.authError(error));
        this.refreshTools();
      },
    });
  }
  private authError(error: HttpErrorResponse): string {
    const title: unknown = error.error?.title;
    return typeof title === 'string' && title.trim()
      ? title
      : 'Impossible de joindre le service de connexion. Consultez les journaux système de ce nœud.';
  }
  revoke(id: string): void {
    if (!this.isAdmin()) return;
    this.aiService.revokeAuth(id).subscribe({
      next: () => {
        this.notifications.show('Connexion locale supprimée.');
        this.refreshTools();
      },
      error: () => this.notifications.show('Impossible de déconnecter le compte.'),
    });
  }
  clusterLabel(id: string): string {
    const view = this.cluster();
    if (!view) return 'Disponibilité à vérifier';
    const count = view.nodes.filter((n) => n.providers.includes(id)).length;
    return count
      ? `${count} nœud${count > 1 ? 's' : ''} configuré${count > 1 ? 's' : ''}`
      : 'Aucune connexion déclarée';
  }
  providerNames(ids: readonly string[]): string {
    return ids.map((id) => this.providers.find((p) => p.id === id)?.name ?? id).join(', ');
  }
  authLabel(state: string): string {
    return (
      (
        {
          authorized: 'Identifiants reconnus',
          pending: 'Autorisation en attente',
          expired: 'Connexion expirée',
          failed: 'Échec de connexion',
        } as Record<string, string>
      )[state] ?? 'Non associé'
    );
  }
}
