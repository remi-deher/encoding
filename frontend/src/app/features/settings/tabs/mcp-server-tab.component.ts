import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatInputModule } from '@angular/material/input';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiTokenResponse } from '../../../shared/models/api-token';
import { McpToolDefinition } from '../../../shared/models/mcp';
import { McpService } from '../../../shared/services/mcp.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { ContextBannerComponent } from '../../../shared/ui/context-banner.component';

type ClientPlatform = 'claude' | 'cursor' | 'antigravity' | 'stdio';

@Component({
  selector: 'app-mcp-server-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ContextBannerComponent,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatTooltipModule,
  ],
  template: `
    <app-context-banner
      label="Model Context Protocol"
      title="Serveur MCP intégré (0061)"
      description="Connectez n’importe quel assistant IA (Antigravity, Claude Desktop, Cursor) en HTTP/SSE ou stdio sans installer d’autre conteneur. L’IA peut inspecter, créer et piloter vos flows selon vos droits."
    >
      <div banner-actions class="banner-actions-row">
        <span class="mcp-status-pill" [class.is-online]="isOnline()">
          <span class="status-dot"></span>
          {{ isOnline() ? 'Serveur MCP actif' : 'Vérification…' }}
        </span>
        <button matButton="tonal" type="button" [disabled]="testing()" (click)="pingServer()">
          {{ testing() ? 'Test en cours…' : 'Tester la connexion' }}
        </button>
      </div>
    </app-context-banner>

    <div class="mcp-grid">
      <!-- Section 1 : Générateur de configuration -->
      <section class="mcp-card config-generator-card">
        <div class="card-header">
          <div class="header-titles">
            <span class="card-eyebrow">Connexion 1-clic</span>
            <h2>Configuration pour votre client IA</h2>
          </div>
          <div class="client-tabs" role="tablist" aria-label="Clients MCP">
            <button
              type="button"
              class="client-tab-btn"
              [class.is-active]="selectedClient() === 'claude'"
              (click)="selectedClient.set('claude')"
            >
              Claude Desktop
            </button>
            <button
              type="button"
              class="client-tab-btn"
              [class.is-active]="selectedClient() === 'cursor'"
              (click)="selectedClient.set('cursor')"
            >
              Cursor / VS Code
            </button>
            <button
              type="button"
              class="client-tab-btn"
              [class.is-active]="selectedClient() === 'antigravity'"
              (click)="selectedClient.set('antigravity')"
            >
              Antigravity
            </button>
            <button
              type="button"
              class="client-tab-btn"
              [class.is-active]="selectedClient() === 'stdio'"
              (click)="selectedClient.set('stdio')"
            >
              Ligne de commande (stdio)
            </button>
          </div>
        </div>

        <div class="card-body">
          <div class="token-selector-row">
            <mat-form-field appearance="outline" subscriptSizing="dynamic" class="token-field">
              <mat-label>Jeton d’authentification inséré dans la config</mat-label>
              <mat-select
                [value]="selectedTokenPrefix()"
                (selectionChange)="selectedTokenPrefix.set($event.value)"
              >
                <mat-option value="">-- Jeton fictif (à remplacer après copie) --</mat-option>
                @for (token of availableTokens(); track token.id) {
                  <mat-option [value]="token.tokenPrefix">
                    {{ token.name }} ({{ token.tokenPrefix }}… · {{ token.role }})
                  </mat-option>
                }
              </mat-select>
            </mat-form-field>

            <button matButton="filled" type="button" (click)="copyConfigSnippet()">
              Copier la configuration
            </button>
          </div>

          <div class="snippet-container">
            <pre class="snippet-pre"><code>{{ configSnippet() }}</code></pre>
          </div>

          <p class="config-instructions">
            <strong>Emplacement :</strong> {{ clientInstructions() }}
          </p>
        </div>
      </section>

      <!-- Section 2 : Catalogue des outils disponibles -->
      <section class="mcp-card tools-catalog-card">
        <div class="card-header">
          <div class="header-titles">
            <span class="card-eyebrow">Outils exposés</span>
            <h2>Outils MCP disponibles ({{ tools().length }})</h2>
          </div>
          <mat-form-field appearance="outline" subscriptSizing="dynamic" class="tool-search-field">
            <mat-label>Filtrer les outils</mat-label>
            <input
              matInput
              type="search"
              [value]="searchQuery()"
              (input)="searchQuery.set($any($event.target).value)"
            />
          </mat-form-field>
        </div>

        <div class="tools-list">
          @for (tool of filteredTools(); track tool.name) {
            <article class="tool-item">
              <div class="tool-item-header">
                <div class="tool-name-group">
                  <span class="tool-glyph" aria-hidden="true">⚡</span>
                  <code class="tool-name">{{ tool.name }}</code>
                </div>
                <span
                  class="role-badge"
                  [class.is-admin]="isAdminTool(tool.name)"
                  [class.is-viewer]="!isAdminTool(tool.name)"
                >
                  {{ isAdminTool(tool.name) ? 'Admin requis' : 'Lecture' }}
                </span>
              </div>

              <p class="tool-desc">{{ tool.description }}</p>

              @if (toolProperties(tool); as props) {
                @if (props.length > 0) {
                  <div class="tool-props">
                    <span class="props-label">Arguments :</span>
                    @for (prop of props; track prop.name) {
                      <span class="prop-chip" [class.is-required]="prop.required">
                        {{ prop.name }}{{ prop.required ? '*' : '' }}
                        <span class="prop-type">({{ prop.type }})</span>
                      </span>
                    }
                  </div>
                }
              }
            </article>
          } @empty {
            <p class="empty-tools">Aucun outil ne correspond à votre recherche.</p>
          }
        </div>
      </section>
    </div>
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-4);
    }

    .banner-actions-row {
      display: flex;
      align-items: center;
      gap: var(--space-3);
    }

    .mcp-status-pill {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      padding: var(--space-1) var(--space-3);
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 600;
      background: var(--surface-container-highest);
      color: var(--text-secondary);
    }

    .mcp-status-pill.is-online {
      background: var(--success-soft);
      color: var(--success);
    }

    .status-dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: currentColor;
    }

    .mcp-grid {
      display: grid;
      gap: var(--space-4);
    }

    .mcp-card {
      padding: var(--space-5);
      border-radius: var(--radius-xl);
      background: var(--surface-container);
      display: grid;
      gap: var(--space-4);
    }

    .card-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: var(--space-3);
    }

    .card-eyebrow {
      font-size: var(--font-label);
      text-transform: uppercase;
      letter-spacing: 0.05em;
      color: var(--primary);
      font-weight: 700;
    }

    .card-header h2 {
      margin: 0;
      font-size: var(--font-title);
      color: var(--text-primary);
    }

    .client-tabs {
      display: flex;
      flex-wrap: wrap;
      gap: var(--space-2);
    }

    .client-tab-btn {
      padding: var(--space-2) var(--space-3);
      border-radius: var(--radius-pill);
      border: 1px solid transparent;
      background: var(--surface-container-high);
      color: var(--text-secondary);
      font-size: var(--font-label);
      cursor: pointer;
      transition: all 0.2s ease;
    }

    .client-tab-btn:hover {
      background: var(--surface-container-highest);
      color: var(--text-primary);
    }

    .client-tab-btn.is-active {
      background: var(--primary-container);
      color: var(--on-primary-container, var(--primary));
      font-weight: 600;
    }

    .token-selector-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: var(--space-3);
      margin-bottom: var(--space-3);
    }

    .token-field {
      flex: 1;
      min-width: 280px;
    }

    .snippet-container {
      border-radius: var(--radius-lg);
      background: var(--surface-container-lowest);
      border: 1px solid var(--outline-variant);
      padding: var(--space-3) var(--space-4);
      overflow-x: auto;
    }

    .snippet-pre {
      margin: 0;
      font-family: ui-monospace, 'JetBrains Mono', 'Consolas', monospace;
      font-size: var(--font-body-sm);
      line-height: 1.5;
      color: var(--text-primary);
    }

    .config-instructions {
      margin: var(--space-2) 0 0 0;
      font-size: var(--font-body-sm);
      color: var(--text-secondary);
    }

    .tools-list {
      display: grid;
      gap: var(--space-3);
    }

    .tool-item {
      padding: var(--space-4);
      border-radius: var(--radius-lg);
      background: var(--surface-container-high);
      display: grid;
      gap: var(--space-2);
    }

    .tool-item-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: var(--space-2);
    }

    .tool-name-group {
      display: flex;
      align-items: center;
      gap: var(--space-2);
    }

    .tool-glyph {
      color: var(--primary);
    }

    .tool-name {
      font-family: ui-monospace, 'JetBrains Mono', monospace;
      font-weight: 700;
      font-size: var(--font-body);
      color: var(--text-primary);
    }

    .role-badge {
      padding: 2px 8px;
      border-radius: var(--radius-pill);
      font-size: var(--font-label);
      font-weight: 600;
    }

    .role-badge.is-admin {
      background: var(--warning-soft);
      color: var(--warning);
    }

    .role-badge.is-viewer {
      background: var(--tertiary-soft);
      color: var(--tertiary);
    }

    .tool-desc {
      margin: 0;
      font-size: var(--font-body-sm);
      color: var(--text-secondary);
      line-height: 1.4;
    }

    .tool-props {
      display: flex;
      align-items: center;
      flex-wrap: wrap;
      gap: var(--space-2);
      margin-top: var(--space-1);
    }

    .props-label {
      font-size: var(--font-label);
      font-weight: 600;
      color: var(--text-secondary);
    }

    .prop-chip {
      padding: 1px 6px;
      border-radius: var(--radius-sm);
      background: var(--surface-container-highest);
      font-size: var(--font-label);
      font-family: ui-monospace, monospace;
      color: var(--text-primary);
    }

    .prop-chip.is-required {
      border: 1px solid var(--accent-subtle);
    }

    .prop-type {
      color: var(--text-secondary);
      font-size: 11px;
    }

    .empty-tools {
      text-align: center;
      color: var(--text-secondary);
      padding: var(--space-4);
    }
  `,
})
export class McpServerTabComponent {
  private readonly mcpService = inject(McpService);
  private readonly notifications = inject(NotificationService);

  readonly tokens = input<readonly ApiTokenResponse[]>([]);

  protected readonly isOnline = signal(true);
  protected readonly testing = signal(false);
  protected readonly selectedClient = signal<ClientPlatform>('claude');
  protected readonly selectedTokenPrefix = signal('');
  protected readonly searchQuery = signal('');
  protected readonly tools = signal<readonly McpToolDefinition[]>([]);

  protected readonly availableTokens = computed(() =>
    this.tokens().filter((t) => t.isActive),
  );

  protected readonly filteredTools = computed(() => {
    const q = this.searchQuery().trim().toLowerCase();
    if (!q) return this.tools();
    return this.tools().filter(
      (t) =>
        t.name.toLowerCase().includes(q) ||
        t.description.toLowerCase().includes(q),
    );
  });

  protected readonly effectiveToken = computed(() => {
    const prefix = this.selectedTokenPrefix();
    return prefix ? `${prefix}<secret_complet>` : 'enc_pat_<votre_jeton_ici>';
  });

  protected readonly serverBaseUrl = computed(() => {
    if (typeof window !== 'undefined' && window.location) {
      return window.location.origin;
    }
    return 'http://localhost:5000';
  });

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
        return `# Exécution en ligne de commande locale ou Docker :\ndocker exec -i encoding-server dotnet /app/api/Encoding.Api.dll mcp \\\n  --url "${baseUrl}" \\\n  --token "${token}"`;
    }
  });

  protected readonly clientInstructions = computed(() => {
    switch (this.selectedClient()) {
      case 'claude':
        return 'Collez ce bloc dans claude_desktop_config.json (%APPDATA%\\Claude\\ ou ~/Library/Application Support/Claude/).';
      case 'cursor':
        return 'Collez ce bloc dans votre fichier .cursor/mcp.json ou dans les paramètres Features > MCP de Cursor.';
      case 'antigravity':
        return 'Déclarez ce serveur dans votre configuration antigravity mcp_servers ou connectez-le au sidecar.';
      case 'stdio':
        return 'À exécuter dans un terminal ayant accès au conteneur Docker Encoding.';
    }
  });

  constructor() {
    void this.loadTools();
    void this.pingServer();
  }

  async loadTools(): Promise<void> {
    try {
      const list = await firstValueFrom(this.mcpService.listTools());
      this.tools.set(list);
    } catch {
      this.tools.set([]);
    }
  }

  async pingServer(): Promise<void> {
    this.testing.set(true);
    try {
      const ok = await firstValueFrom(this.mcpService.ping());
      this.isOnline.set(ok);
      if (ok) {
        this.notifications.show('Le serveur MCP répond normalement.');
      } else {
        this.notifications.show('Le serveur MCP n’a pas répondu au ping.');
      }
    } catch {
      this.isOnline.set(false);
      this.notifications.show('Erreur de communication avec le serveur MCP.');
    } finally {
      this.testing.set(false);
    }
  }

  protected async copyConfigSnippet(): Promise<void> {
    try {
      await navigator.clipboard.writeText(this.configSnippet());
      this.notifications.show('Configuration copiée dans le presse-papier.');
    } catch {
      this.notifications.show('Erreur lors de la copie.');
    }
  }

  protected isAdminTool(toolName: string): boolean {
    const adminTools = [
      'create_flow',
      'clone_flow',
      'delete_flow',
      'save_script',
      'publish_script',
      'save_encoding_profile',
      'publish_encoding_profile',
    ];
    return adminTools.includes(toolName);
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
