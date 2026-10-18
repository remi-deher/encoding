import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  output,
  signal,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiTokenResponse } from '../../../shared/models/api-token';
import { ApiTokenService } from '../../../shared/services/api-token.service';
import { NotificationService } from '../../../shared/services/notification.service';
import { ContextBannerComponent } from '../../../shared/ui/context-banner.component';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
  DataTableRowAction,
} from '../../../shared/ui/data-table.component';
import { RecordFormComponent, RecordFormData } from '../../../shared/ui/record-form.component';

interface ApiTokenRow extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly prefix: string;
  readonly role: 'admin' | 'viewer';
  readonly roleLabel: string;
  readonly statusLabel: string;
  readonly isActive: boolean;
  readonly createdLabel: string;
  readonly lastUsedLabel: string;
  readonly expiresLabel: string;
}

@Component({
  selector: 'app-api-token-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ContextBannerComponent,
    DataTableComponent,
    MatButtonModule,
    MatTooltipModule,
    RecordFormComponent,
  ],
  template: `
    <app-context-banner
      label="Automatisation et agents IA"
      title="Jetons d’API Cluster (0060)"
      description="Les jetons permettent aux assistants IA (Antigravity, Claude Desktop, Cursor), scripts et outils tiers de piloter le cluster sans session de navigateur. Ils sont réconciliés sur l’ensemble des nœuds serveurs."
    >
      <div banner-actions>
        <button matButton="filled" type="button" (click)="openCreate()">
          Nouveau jeton
        </button>
      </div>
    </app-context-banner>

    @if (revealedSecret(); as secret) {
      <section class="secret-reveal-card" aria-labelledby="secret-title">
        <div class="secret-reveal-header">
          <span class="secret-icon" aria-hidden="true">🔑</span>
          <div>
            <h3 id="secret-title">Nouveau jeton créé avec succès</h3>
            <p>
              Ce jeton ne sera <strong>plus jamais réaffiché</strong>. Copiez-le dès maintenant et insérez-le dans vos configurations d’agents ou de scripts.
            </p>
          </div>
        </div>

        <div class="secret-box">
          <code class="secret-value">{{ secret }}</code>
          <button matButton="tonal" type="button" (click)="copySecret(secret)">
            Copier le jeton
          </button>
        </div>

        <div class="secret-footer">
          <button matButton="filled" type="button" (click)="dismissSecret()">
            J’ai bien copié mon jeton
          </button>
        </div>
      </section>
    }

    <app-data-table
      layout="resources"
      catalogue-table
      [columns]="columns"
      [rows]="filteredRows()"
      [rowActions]="actions"
      (rowAction)="onAction($event)"
      emptyMessage="Aucun jeton d’API configuré. Créez-en un pour autoriser des agents IA ou scripts."
    />

    @if (formOpen()) {
      <section class="token-form-drawer" aria-labelledby="token-form-title">
        <app-record-form
          [data]="formData"
          [busy]="busy()"
          (submitted)="submitCreate($event)"
          (cancelled)="closeCreate()"
        />
      </section>
    }
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-4);
    }

    .secret-reveal-card {
      padding: var(--space-4) var(--space-5);
      border-radius: var(--radius-xl);
      background: var(--surface-container-high);
      border: 1px solid var(--accent-subtle);
      display: grid;
      gap: var(--space-3);
    }

    .secret-reveal-header {
      display: flex;
      align-items: flex-start;
      gap: var(--space-3);
    }

    .secret-icon {
      font-size: 24px;
      line-height: 1;
    }

    .secret-reveal-header h3 {
      margin: 0 0 var(--space-1) 0;
      font-size: var(--font-title);
      color: var(--text-primary);
    }

    .secret-reveal-header p {
      margin: 0;
      font-size: var(--font-body-sm);
      color: var(--text-secondary);
    }

    .secret-box {
      display: flex;
      align-items: center;
      gap: var(--space-3);
      padding: var(--space-3);
      border-radius: var(--radius-lg);
      background: var(--surface-container-highest);
      overflow-x: auto;
    }

    .secret-value {
      flex: 1;
      font-family: ui-monospace, 'JetBrains Mono', 'Consolas', monospace;
      font-size: var(--font-body-sm);
      color: var(--primary);
      word-break: break-all;
      user-select: all;
    }

    .secret-footer {
      display: flex;
      justify-content: flex-end;
    }

    .token-form-drawer {
      padding: var(--space-4);
      background: var(--surface-container);
      border-radius: var(--radius-xl);
      margin-top: var(--space-2);
    }
  `,
})
export class ApiTokenTabComponent {
  private readonly tokenService = inject(ApiTokenService);
  private readonly notifications = inject(NotificationService);

  readonly tokensUpdated = output<readonly ApiTokenResponse[]>();

  protected readonly tokens = signal<readonly ApiTokenResponse[]>([]);
  protected readonly busy = signal(false);
  protected readonly formOpen = signal(false);
  protected readonly revealedSecret = signal<string | null>(null);
  protected readonly query = signal('');

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du jeton', sortable: true },
    { key: 'prefix', label: 'Préfixe' },
    { key: 'roleLabel', label: 'Rôle', sortable: true },
    { key: 'statusLabel', label: 'État', sortable: true },
    { key: 'lastUsedLabel', label: 'Dernier usage', sortable: true },
    { key: 'expiresLabel', label: 'Expiration', sortable: true },
    { key: 'createdLabel', label: 'Créé le', sortable: true },
  ];

  protected readonly actions: readonly DataTableRowAction[] = [
    {
      id: 'revoke',
      label: 'Révoquer le jeton',
      tone: 'danger',
      visibleWhen: (row) => !!row['isActive'],
      confirmation: (row) => ({
        title: 'Révoquer ce jeton d’API ?',
        message: `Les systèmes utilisant « ${row['name']} » ne pourront plus s’authentifier sur le cluster. Cette action est irréversible.`,
        confirmLabel: 'Révoquer définitivement',
      }),
    },
  ];

  protected readonly rows = computed<readonly ApiTokenRow[]>(() =>
    this.tokens().map((token) => ({
      id: token.id,
      name: token.name,
      prefix: token.tokenPrefix ? `${token.tokenPrefix}…` : '—',
      role: token.role,
      roleLabel: token.role === 'admin' ? 'Administrateur' : 'Lecture seule',
      isActive: token.isActive,
      statusLabel: token.isActive ? 'Actif' : 'Révoqué',
      lastUsedLabel: token.lastUsedAt
        ? new Date(token.lastUsedAt).toLocaleString('fr-FR')
        : 'Jamais utilisé',
      expiresLabel: token.expiresAt
        ? new Date(token.expiresAt).toLocaleDateString('fr-FR')
        : 'Illimité',
      createdLabel: new Date(token.createdAt).toLocaleDateString('fr-FR'),
    })),
  );

  protected readonly filteredRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return this.rows().filter(
      (row) => !q || row.name.toLowerCase().includes(q) || row.prefix.toLowerCase().includes(q),
    );
  });

  protected readonly formData: RecordFormData = {
    title: 'Nouveau jeton d’API',
    description: 'Créez un jeton Bearer pour connecter un agent IA, un IDE ou un script d’automatisation.',
    submitLabel: 'Générer le jeton',
    review: false,
    fields: [
      {
        id: 'name',
        label: 'Nom ou description du jeton',
        type: 'text',
        required: true,
        placeholder: 'ex. Claude Desktop, Antigravity, Script Cron Nightly',
      },
      {
        id: 'role',
        label: 'Rôle et permissions',
        type: 'select',
        required: true,
        options: [
          { value: 'admin', label: 'Administrateur (Lecture, écriture et gestion des flows)' },
          { value: 'viewer', label: 'Lecture seule (Consultation sans modification)' },
        ],
      },
      {
        id: 'duration',
        label: 'Validité du jeton',
        type: 'select',
        required: true,
        options: [
          { value: 'never', label: 'Illimité (ne expire jamais)' },
          { value: '30', label: '30 jours' },
          { value: '90', label: '90 jours' },
          { value: '365', label: '1 an' },
        ],
      },
    ],
    initialValues: { role: 'admin', duration: 'never' },
  };

  constructor() {
    void this.refresh();
  }

  async refresh(): Promise<void> {
    try {
      const list = await firstValueFrom(this.tokenService.list());
      this.tokens.set(list);
      this.tokensUpdated.emit(list);
    } catch {
      this.notifications.show('Impossible de récupérer les jetons d’API.');
    }
  }

  protected openCreate(): void {
    this.formOpen.set(true);
  }

  protected closeCreate(): void {
    this.formOpen.set(false);
  }

  protected dismissSecret(): void {
    this.revealedSecret.set(null);
  }

  protected async copySecret(secret: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(secret);
      this.notifications.show('Jeton copié dans le presse-papier.');
    } catch {
      this.notifications.show('Erreur lors de la copie du jeton.');
    }
  }

  protected async submitCreate(values: Record<string, string>): Promise<void> {
    this.busy.set(true);
    try {
      const name = values['name'].trim();
      const role = (values['role'] as 'admin' | 'viewer') || 'admin';
      const duration = values['duration'];

      let expiresAt: string | null = null;
      if (duration && duration !== 'never') {
        const days = parseInt(duration, 10);
        const date = new Date();
        date.setDate(date.getDate() + days);
        expiresAt = date.toISOString();
      }

      const response = await firstValueFrom(
        this.tokenService.create({ name, role, expiresAt }),
      );

      this.notifications.show(`Jeton « ${name} » généré avec succès.`);
      this.revealedSecret.set(response.secret);
      this.closeCreate();
      await this.refresh();
    } catch {
      this.notifications.show('Échec de la génération du jeton.');
    } finally {
      this.busy.set(false);
    }
  }

  protected async onAction(event: DataTableActionEvent): Promise<void> {
    const row = event.row as ApiTokenRow;
    if (event.actionId === 'revoke') {
      try {
        await firstValueFrom(this.tokenService.revoke(row.id));
        this.notifications.show(`Le jeton « ${row.name} » a été révoqué.`);
        await this.refresh();
      } catch {
        this.notifications.show('Échec de la révocation du jeton.');
      }
    }
  }
}
