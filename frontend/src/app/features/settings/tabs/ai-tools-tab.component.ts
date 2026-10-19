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
  templateUrl: './ai-tools-tab.component.html',
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
  readonly claudeToken = signal('');

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
      (this.fingerprint() !== this.savedFingerprint || this.claudeToken().length > 0)
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
  saveClaudeToken(): void {
    if (!this.isAdmin()) return;
    this.connecting.set(true);
    this.aiService.saveClaudeToken(this.claudeToken().trim()).subscribe({
      next: () => {
        this.claudeToken.set('');
        this.connecting.set(false);
        this.notifications.show('Connexion Claude enregistrée sur ce nœud.');
        this.refreshTools();
      },
      error: (error: HttpErrorResponse) => {
        this.claudeToken.set('');
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
