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
import { MatDialog } from '@angular/material/dialog';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ApiTokenResponse } from '../../../shared/models/api-token';
import { formatRelativeTime } from '../../../shared/models/date-format';
import { ApiTokenService } from '../../../shared/services/api-token.service';
import { NotificationService } from '../../../shared/services/notification.service';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
  DataTableRowAction,
} from '../../../shared/ui/data-table.component';
import {
  CreateApiTokenDialogComponent,
  CreateApiTokenDialogResult,
} from '../dialogs/create-api-token-dialog.component';
import {
  ApiTokenSecretDialogComponent,
  ApiTokenSecretDialogData,
} from '../dialogs/api-token-secret-dialog.component';

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
    DataTableComponent,
    MatButtonModule,
    MatTooltipModule,
  ],
  template: `
    <div class="tab-header">
      <div class="tab-titles">
        <h2>Jetons d’API</h2>
        <p>Gérez les accès Bearer pour les scripts, outils externes, CI/CD et agents IA.</p>
      </div>
      <button matButton="filled" type="button" (click)="openCreate()">
        Générer un jeton
      </button>
    </div>

    <app-data-table
      layout="resources"
      catalogue-table
      [columns]="columns"
      [rows]="filteredRows()"
      [rowActions]="actions"
      (rowAction)="onAction($event)"
      emptyMessage="Aucun jeton d’API actif. Générez-en un pour autoriser des accès programmatiques."
    />
  `,
  styles: `
    :host {
      display: grid;
      gap: var(--space-3);
    }

    .tab-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      flex-wrap: wrap;
      gap: var(--space-3);
      padding: var(--space-2) 0;
    }

    .tab-titles h2 {
      margin: 0;
      font-size: var(--font-title);
      color: var(--text-primary);
    }

    .tab-titles p {
      margin: var(--space-1) 0 0 0;
      font-size: var(--font-body-sm);
      color: var(--text-secondary);
    }
  `,
})
export class ApiTokenTabComponent {
  private readonly tokenService = inject(ApiTokenService);
  private readonly notifications = inject(NotificationService);
  private readonly dialog = inject(MatDialog);

  readonly tokensUpdated = output<readonly ApiTokenResponse[]>();

  protected readonly tokens = signal<readonly ApiTokenResponse[]>([]);
  protected readonly query = signal('');

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom du jeton', sortable: true },
    { key: 'prefix', label: 'Préfixe' },
    { key: 'roleLabel', label: 'Rôle', sortable: true },
    { key: 'statusLabel', label: 'État', sortable: true },
    { key: 'lastUsedLabel', label: 'Dernière utilisation', sortable: true },
    { key: 'expiresLabel', label: 'Expiration', sortable: true },
  ];

  protected readonly actions: readonly DataTableRowAction[] = [
    {
      id: 'revoke',
      label: 'Révoquer',
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
      lastUsedLabel: token.lastUsedAt ? formatRelativeTime(token.lastUsedAt) : 'Jamais utilisé',
      expiresLabel: token.expiresAt
        ? new Date(token.expiresAt).toLocaleDateString('fr-FR')
        : 'N’expire jamais',
      createdLabel: new Date(token.createdAt).toLocaleDateString('fr-FR'),
    })),
  );

  protected readonly filteredRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return this.rows().filter(
      (row) => !q || row.name.toLowerCase().includes(q) || row.prefix.toLowerCase().includes(q),
    );
  });

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
    const dialogRef = this.dialog.open<CreateApiTokenDialogComponent, undefined, CreateApiTokenDialogResult | null>(
      CreateApiTokenDialogComponent,
      {
        width: '460px',
      },
    );

    dialogRef.afterClosed().subscribe(async (result) => {
      if (!result) return;
      try {
        let expiresAt: string | null = null;
        if (result.expiresInDays) {
          const date = new Date();
          date.setDate(date.getDate() + result.expiresInDays);
          expiresAt = date.toISOString();
        }

        const res = await firstValueFrom(
          this.tokenService.create({
            name: result.name,
            role: result.role,
            expiresAt,
          }),
        );

        this.notifications.show(`Jeton « ${result.name} » généré.`);
        await this.refresh();

        this.dialog.open<ApiTokenSecretDialogComponent, ApiTokenSecretDialogData>(
          ApiTokenSecretDialogComponent,
          {
            width: '500px',
            data: {
              name: result.name,
              secret: res.secret,
            },
          },
        );
      } catch {
        this.notifications.show('Échec de la génération du jeton.');
      }
    });
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
