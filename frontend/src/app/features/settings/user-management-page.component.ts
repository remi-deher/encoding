import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../../core/auth/auth.service';
import { MINIMUM_PASSWORD_LENGTH, USER_ROLE_LABELS, UserAccount } from '../../shared/models/auth';
import { NotificationService } from '../../shared/services/notification.service';
import { UserAccounts } from '../../shared/services/user-accounts';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import {
  DataTableActionEvent,
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
  DataTableRowAction,
} from '../../shared/ui/data-table.component';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';

interface UserRow extends DataTableRow {
  readonly id: string;
  readonly username: string;
  readonly roleLabel: string;
  readonly role: string;
  readonly statusLabel: string;
  readonly isActive: boolean;
  readonly isSelf: boolean;
  readonly createdLabel: string;
}

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-user-management-page',
  imports: [
    DataTableComponent,
    ManagementCatalogTemplateComponent,
    MatButtonModule,
    RecordFormComponent,
  ],
  template: `
    <app-management-catalog-template
      title="Paramètres"
      filterLabel="Utilisateurs"
      searchLabel="Rechercher un utilisateur"
      drawerSize="standard"
      [searchQuery]="query()"
      (searchQueryChange)="query.set($event)"
      [catalogState]="state()"
      [actionOpen]="formOpen()"
      [actionTitle]="resetTarget() ? 'Réinitialiser le mot de passe' : 'Nouvel utilisateur'"
      (actionClosed)="closeForm()"
    >
      <ng-template #pageActions>
        <button matButton="filled" type="button" (click)="newUser()">Nouvel utilisateur</button>
      </ng-template>
      <app-data-table
        layout="resources"
        catalogue-table
        [columns]="columns"
        [rows]="filteredRows()"
        [rowActions]="actions"
        (rowAction)="onAction($event)"
        emptyMessage="Aucun utilisateur."
      />
      <ng-template #actionContent>
        <app-record-form
          [data]="form()"
          [busy]="busy()"
          (submitted)="submit($event)"
          (cancelled)="closeForm()"
        />
      </ng-template>
    </app-management-catalog-template>
  `,
})
export class UserManagementPageComponent {
  private readonly accounts = inject(UserAccounts);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);
  private readonly users = signal<readonly UserAccount[]>([]);
  protected readonly state = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly query = signal('');
  protected readonly busy = signal(false);
  protected readonly formOpen = signal(false);
  protected readonly resetTarget = signal<UserRow | null>(null);

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'username', label: 'Utilisateur', sortable: true },
    { key: 'roleLabel', label: 'Rôle', sortable: true },
    { key: 'statusLabel', label: 'État', sortable: true },
    { key: 'createdLabel', label: 'Créé le', sortable: true },
  ];
  protected readonly actions: readonly DataTableRowAction[] = [
    {
      id: 'make-admin',
      label: 'Passer administrateur',
      visibleWhen: (row) => row['role'] === 'viewer',
    },
    {
      id: 'make-viewer',
      label: 'Passer en lecture seule',
      visibleWhen: (row) => row['role'] === 'admin' && !row['isSelf'],
      confirmation: (row) => ({
        title: 'Retirer les droits d’administration ?',
        message: `« ${row['username']} » pourra consulter le cluster sans pouvoir le modifier.`,
        confirmLabel: 'Passer en lecture seule',
      }),
    },
    {
      id: 'deactivate',
      label: 'Désactiver',
      visibleWhen: (row) => !!row['isActive'] && !row['isSelf'],
      confirmation: (row) => ({
        title: 'Désactiver ce compte ?',
        message: `Les sessions de « ${row['username']} » seront fermées immédiatement.`,
        confirmLabel: 'Désactiver',
      }),
    },
    { id: 'activate', label: 'Réactiver', visibleWhen: (row) => !row['isActive'] },
    { id: 'password', label: 'Réinitialiser le mot de passe' },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      visibleWhen: (row) => !row['isSelf'],
      confirmation: (row) => ({
        title: 'Supprimer ce compte ?',
        message: `« ${row['username']} » ne pourra plus se connecter. Cette action est définitive.`,
        confirmLabel: 'Supprimer',
      }),
    },
  ];

  protected readonly rows = computed<readonly UserRow[]>(() =>
    this.users().map((user) => ({
      id: user.id,
      username: user.username,
      role: user.role,
      roleLabel: USER_ROLE_LABELS[user.role],
      isActive: user.isActive,
      statusLabel: user.isActive ? 'Actif' : 'Désactivé',
      isSelf: user.id === this.auth.user()?.id,
      createdLabel: new Date(user.createdAt).toLocaleDateString('fr-FR'),
    })),
  );
  protected readonly filteredRows = computed(() => {
    const query = this.query().trim().toLowerCase();
    return this.rows().filter((row) => !query || row.username.toLowerCase().includes(query));
  });
  protected readonly form = computed<RecordFormData>(() => {
    const target = this.resetTarget();
    return target
      ? {
          title: 'Réinitialiser le mot de passe',
          description: `Nouveau mot de passe de « ${target.username} ». Ses sessions seront fermées.`,
          submitLabel: 'Réinitialiser',
          fields: [
            {
              id: 'password',
              label: 'Nouveau mot de passe',
              type: 'password',
              required: true,
              autocomplete: 'new-password',
              hint: `Au moins ${MINIMUM_PASSWORD_LENGTH} caractères.`,
            },
          ],
        }
      : {
          title: 'Nouvel utilisateur',
          description: 'Créez un compte pour un membre de l’équipe.',
          submitLabel: 'Créer l’utilisateur',
          fields: [
            {
              id: 'username',
              label: 'Nom d’utilisateur',
              type: 'text',
              required: true,
              maxLength: 64,
              autocomplete: 'off',
            },
            {
              id: 'password',
              label: 'Mot de passe initial',
              type: 'password',
              required: true,
              autocomplete: 'new-password',
              hint: `Au moins ${MINIMUM_PASSWORD_LENGTH} caractères.`,
            },
            {
              id: 'role',
              label: 'Rôle',
              type: 'select',
              required: true,
              options: [
                { value: 'viewer', label: USER_ROLE_LABELS.viewer },
                { value: 'admin', label: USER_ROLE_LABELS.admin },
              ],
            },
          ],
          initialValues: { role: 'viewer' },
        };
  });

  constructor() {
    void this.refresh();
  }

  private async refresh(): Promise<void> {
    try {
      this.users.set(await firstValueFrom(this.accounts.list()));
      this.state.set('ready');
    } catch {
      this.state.set(this.users().length ? 'ready' : 'error');
    }
  }

  protected newUser(): void {
    this.resetTarget.set(null);
    this.formOpen.set(true);
  }

  protected closeForm(): void {
    this.formOpen.set(false);
    this.resetTarget.set(null);
  }

  protected async onAction(event: DataTableActionEvent): Promise<void> {
    const row = event.row as UserRow;
    switch (event.actionId) {
      case 'password':
        this.resetTarget.set(row);
        this.formOpen.set(true);
        return;
      case 'make-admin':
        return this.run(
          this.accounts.update(row.id, 'admin', row.isActive),
          `« ${row.username} » est administrateur.`,
        );
      case 'make-viewer':
        return this.run(
          this.accounts.update(row.id, 'viewer', row.isActive),
          `« ${row.username} » est en lecture seule.`,
        );
      case 'deactivate':
        return this.run(
          this.accounts.update(row.id, row.role as 'admin' | 'viewer', false),
          `« ${row.username} » est désactivé.`,
        );
      case 'activate':
        return this.run(
          this.accounts.update(row.id, row.role as 'admin' | 'viewer', true),
          `« ${row.username} » est réactivé.`,
        );
      case 'delete':
        return this.run(this.accounts.remove(row.id), `« ${row.username} » a été supprimé.`);
    }
  }

  protected async submit(value: Record<string, string>): Promise<void> {
    const target = this.resetTarget();
    this.busy.set(true);
    try {
      if (target) {
        await firstValueFrom(this.accounts.resetPassword(target.id, value['password']));
        this.notifications.show(`Le mot de passe de « ${target.username} » a été réinitialisé.`);
      } else {
        await firstValueFrom(
          this.accounts.create(
            value['username'].trim(),
            value['password'],
            value['role'] as 'admin' | 'viewer',
          ),
        );
        this.notifications.show(`« ${value['username'].trim()} » a été créé.`);
      }
      this.closeForm();
      await this.refresh();
    } catch (error) {
      this.notifications.show(AuthService.errorMessage(error, 'L’opération a échoué.'));
    } finally {
      this.busy.set(false);
    }
  }

  private async run(request: Observable<unknown>, success: string): Promise<void> {
    try {
      await firstValueFrom(request);
      this.notifications.show(success);
      await this.refresh();
    } catch (error) {
      this.notifications.show(AuthService.errorMessage(error, 'L’opération a échoué.'));
    }
  }
}
