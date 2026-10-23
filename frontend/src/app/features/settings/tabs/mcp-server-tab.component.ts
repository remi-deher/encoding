import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  computed,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { HttpClient } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatExpansionModule } from '@angular/material/expansion';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { RouterLink } from '@angular/router';
import {
  SettingGroupComponent,
  SettingRowComponent,
} from '../../../shared/ui/settings/setting-row.component';
import { SettingsEditor } from '../settings-editor';
import { McpToolDefinition } from '../../../shared/models/mcp';
import { ClipboardService } from '../../../shared/services/clipboard.service';
import { McpService } from '../../../shared/services/mcp.service';
import { PublicUrlService } from '../../../shared/services/public-url.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { SearchFieldComponent } from '../../../shared/ui/search-field.component';

type ClientPlatform = 'claude' | 'cursor' | 'antigravity' | 'stdio';

@Component({
  selector: 'app-mcp-server-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatExpansionModule,
    SearchFieldComponent,
    ConfigurationSectionComponent,
    SettingGroupComponent,
    SettingRowComponent,
    RouterLink,
  ],
  template: `
    <app-configuration-section
      title="Assistants externes (MCP)"
      description="Donnez accès au cluster à votre assistant ou à votre éditeur. Lecture seule par défaut."
      icon="terminal"
    >
      <button section-actions matButton="tonal" type="button" (click)="openClient()">
        Configurer un client
      </button>
      <app-setting-group>
        <app-setting-row label="Adresse de connexion">
          <ng-container row-detail>
            @if (addressRevealed()) {
              <code>{{ serverBaseUrl() }}/api/v1/mcp/sse</code>
            }
          </ng-container>
          @if (addressRevealed()) {
            <button matButton="tonal" type="button" (click)="copyAddress()">Copier</button>
          } @else {
            <mat-form-field appearance="outline">
              <mat-label>Mot de passe du compte</mat-label>
              <input
                matInput
                type="password"
                autocomplete="current-password"
                [value]="reauthPassword()"
                (input)="reauthPassword.set($any($event.target).value)"
              />
            </mat-form-field>
            <button
              matButton="tonal"
              type="button"
              [disabled]="reauthBusy()"
              (click)="revealAddress()"
            >
              Vérifier et afficher
            </button>
          }
        </app-setting-row>
        <app-setting-row
          label="Authentification"
          hint="Par jeton d’API. Un jeton administrateur n’écrit que s’il a reçu l’écriture MCP à sa création."
        >
          <a matButton="outlined" routerLink="/settings" [queryParams]="{ tab: 'access' }"
            >Gérer les jetons</a
          >
        </app-setting-row>
        <mat-expansion-panel class="catalog"
          ><mat-expansion-panel-header
            ><mat-panel-title
              >Options avancées : catalogue des outils</mat-panel-title
            ></mat-expansion-panel-header
          >
          <app-search-field
            variant="filled"
            label="Filtrer les outils"
            [value]="searchQuery()"
            (valueChange)="searchQuery.set($event)"
          />
          @for (tool of filteredTools(); track tool.name) {
            <article class="tool">
              <h3>
                <code>{{ tool.name }}</code>
              </h3>
              <p>{{ tool.description }}</p>
              @for (property of toolProperties(tool); track property.name) {
                <span class="argument"
                  >{{ property.name }}{{ property.required ? ' (requis)' : '' }} ·
                  {{ property.type }}</span
                >
              }
            </article>
          } @empty {
            <p>Aucun outil disponible avec ces critères.</p>
          }
        </mat-expansion-panel>
      </app-setting-group>
    </app-configuration-section>
    <ng-template #clientEditor
      ><div class="client-editor">
        <p>
          Choisissez votre client et renseignez la valeur complète d’un jeton, ou remplacez le
          marqueur dans l’exemple.
        </p>
        <mat-form-field appearance="outline"
          ><mat-label>Client</mat-label
          ><mat-select
            [value]="selectedClient()"
            (selectionChange)="selectedClient.set($event.value)"
            ><mat-option value="claude">Claude Desktop</mat-option
            ><mat-option value="cursor">Cursor / VS Code</mat-option
            ><mat-option value="antigravity">Antigravity</mat-option
            ><mat-option value="stdio">Stdio (CLI)</mat-option></mat-select
          ></mat-form-field
        >
        <mat-form-field appearance="outline"
          ><mat-label>Jeton d’API complet (facultatif)</mat-label
          ><input
            matInput
            type="password"
            autocomplete="off"
            [value]="clientToken()"
            (input)="clientToken.set($any($event.target).value)"
          /><mat-hint>Un préfixe ne suffit pas pour s’authentifier.</mat-hint></mat-form-field
        >
        <pre><code>{{ configSnippet() }}</code></pre>
        <p>{{ clientInstructions() }}</p>
        <button matButton="filled" (click)="copyConfigSnippet()">
          {{ clientToken() ? 'Copier la configuration' : 'Copier l’exemple' }}
        </button>
      </div></ng-template
    >
  `,
  styles: `
    :host {
      display: block;
    }
    .endpoint {
      display: flex;
      justify-content: space-between;
      gap: var(--space-4);
      align-items: center;
      flex-wrap: wrap;
      background: var(--surface-container-low);
      padding: var(--space-4);
      border-radius: var(--radius-md);
    }
    .endpoint span {
      display: block;
      margin-bottom: var(--space-2);
      font-size: var(--font-label);
    }
    code {
      overflow-wrap: anywhere;
    }
    p {
      color: var(--text-secondary);
      line-height: 1.6;
    }
    .note {
      font-size: var(--font-label);
    }
    .mat-expansion-panel.catalog {
      box-shadow: none;
      background: var(--surface-container-low);
      border-radius: var(--radius-sm) var(--radius-sm) var(--radius-xl) var(--radius-xl);
    }
    .client-editor {
      display: grid;
      gap: var(--space-4);
    }
    pre {
      margin: 0;
      white-space: pre-wrap;
      overflow-wrap: anywhere;
      padding: var(--space-4);
      background: var(--surface-container-low);
      border-radius: var(--radius-md);
    }
    .tool {
      margin-top: var(--space-4);
    }
    .tool h3 {
      font-size: var(--font-body);
    }
    .argument {
      display: inline-block;
      font-size: var(--font-label);
      margin-right: var(--space-3);
    }
  `,
})
export class McpServerTabComponent {
  private readonly mcpService = inject(McpService);
  private readonly http = inject(HttpClient);
  private readonly notifications = inject(NotificationService);
  private readonly clipboardService = inject(ClipboardService);
  protected readonly addressRevealed = signal(false);
  protected readonly reauthPassword = signal('');
  protected readonly reauthBusy = signal(false);

  private readonly editor = inject(SettingsEditor);
  private readonly clientEditor = viewChild.required<TemplateRef<unknown>>('clientEditor');
  protected readonly clientToken = signal('');
  protected openClient(): void {
    if (!this.addressRevealed()) {
      this.notifications.show('Vérifiez votre mot de passe pour afficher l’adresse MCP.');
      return;
    }
    void this.editor.open({
      title: 'Configurer un client MCP',
      content: this.clientEditor(),
      onClosed: () => this.clientToken.set(''),
    });
  }
  protected async copyAddress(): Promise<void> {
    this.notifications.show(
      (await this.clipboardService.copy(`${this.serverBaseUrl()}/api/v1/mcp/sse`))
        ? 'Adresse copiée.'
        : 'Impossible de copier l’adresse.',
    );
  }

  protected async revealAddress(): Promise<void> {
    this.reauthBusy.set(true);
    try {
      await firstValueFrom(
        this.http.post<void>('/api/v1/auth/reauthenticate', {
          currentPassword: this.reauthPassword(),
        }),
      );
      this.addressRevealed.set(true);
      this.reauthPassword.set('');
    } catch {
      this.notifications.show('Mot de passe incorrect : adresse MCP non affichée.');
    } finally {
      this.reauthBusy.set(false);
    }
  }

  protected readonly selectedClient = signal<ClientPlatform>('claude');
  protected readonly searchQuery = signal('');
  protected readonly tools = signal<readonly McpToolDefinition[]>([]);

  protected readonly filteredTools = computed(() => {
    const q = this.searchQuery().trim().toLowerCase();
    if (!q) return this.tools();
    return this.tools().filter(
      (t) => t.name.toLowerCase().includes(q) || t.description.toLowerCase().includes(q),
    );
  });

  protected readonly effectiveToken = computed(
    () => this.clientToken().trim() || '<JETON_API_COMPLET>',
  );

  private readonly publicUrl = inject(PublicUrlService);
  protected readonly serverBaseUrl = computed(
    () => this.publicUrl.origin() || 'http://localhost:5000',
  );

  protected readonly configSnippet = computed(() => {
    const baseUrl = this.serverBaseUrl();
    const token = this.effectiveToken();
    const client = this.selectedClient();

    switch (client) {
      case 'claude':
        return JSON.stringify(
          {
            mcpServers: {
              encoding: {
                url: `${baseUrl}/api/v1/mcp/sse`,
                headers: {
                  Authorization: `Bearer ${token}`,
                },
              },
            },
          },
          null,
          2,
        );

      case 'cursor':
        return JSON.stringify(
          {
            mcpServers: {
              encoding: {
                url: `${baseUrl}/api/v1/mcp/sse`,
                headers: {
                  Authorization: `Bearer ${token}`,
                },
              },
            },
          },
          null,
          2,
        );

      case 'antigravity':
        return JSON.stringify(
          {
            serverName: 'encoding_mcp',
            transport: 'sse',
            url: `${baseUrl}/api/v1/mcp/sse`,
            headers: {
              Authorization: `Bearer ${token}`,
            },
          },
          null,
          2,
        );

      case 'stdio':
        return `docker exec -i encoding-server dotnet /app/api/Encoding.Api.dll mcp \\\n  --url "${baseUrl}" \\\n  --token "${token}"`;
    }
  });

  protected readonly clientInstructions = computed(() => {
    switch (this.selectedClient()) {
      case 'claude':
        return 'claude_desktop_config.json (%APPDATA%\\Claude\\ ou ~/Library/Application Support/Claude/).';
      case 'cursor':
        return '.cursor/mcp.json ou paramètres Features > MCP de Cursor.';
      case 'antigravity':
        return 'Configuration Antigravity mcp_servers ou sidecar.';
      case 'stdio':
        return 'Ligne de commande terminal.';
    }
  });

  constructor() {
    void this.loadTools();
  }

  async loadTools(): Promise<void> {
    try {
      const list = await firstValueFrom(this.mcpService.listTools());
      this.tools.set(list);
    } catch {
      this.tools.set([]);
    }
  }

  protected async copyConfigSnippet(): Promise<void> {
    const ok = await this.clipboardService.copy(this.configSnippet());
    if (ok) {
      this.notifications.show('Configuration copiée dans le presse-papier.');
    } else {
      this.notifications.show('Erreur lors de la copie.');
    }
  }

  protected toolProperties(
    tool: McpToolDefinition,
  ): readonly { name: string; type: string; required: boolean }[] {
    const props = tool.inputSchema?.properties;
    if (!props) return [];
    const required = tool.inputSchema.required ?? [];
    return Object.entries(props).map(([k, v]) => ({
      name: k,
      type: v.type ?? 'string',
      required: required.includes(k),
    }));
  }
}
