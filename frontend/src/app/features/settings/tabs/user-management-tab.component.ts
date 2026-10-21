import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  output,
  signal,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { firstValueFrom, Observable } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { PasswordChangeDialogComponent } from '../../../core/auth/password-change-dialog.component';
import { ConfigurationSectionComponent } from '../../../shared/ui/configuration-section.component';
import { SettingsEditor } from '../settings-editor';
import { AuthService } from '../../../core/auth/auth.service';
import {
  MINIMUM_PASSWORD_LENGTH,
  USER_ROLE_LABELS,
  UserAccount,
} from '../../../shared/models/auth';
import { NotificationService } from '../../../shared/services/notification.service';
import { UserAccounts } from '../../../shared/services/user-accounts';
import {
  AccessListAction,
  AccessListActionEvent,
  AccessListComponent,
  AccessListRow,
} from '../../../shared/ui/access-list.component';
import { RecordFormComponent, RecordFormData } from '../../../shared/ui/record-form.component';

interface UserRow extends AccessListRow {
  readonly username: string;
  readonly role: string;
  readonly isActive: boolean;
  readonly isSelf: boolean;
}

@Component({
  selector: 'app-user-management-tab',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ConfigurationSectionComponent,
    AccessListComponent,
    MatButtonModule,
    RecordFormComponent,
  ],
  template: `
    <app-configuration-section
      title="Utilisateurs"
      description="Les personnes qui peuvent accéder au cluster."
    >
      <button section-actions matButton="filled" type="button" (click)="newUser()">
        Créer un compte
      </button>
      @if (state() === 'loading') {
        <p role="status">Chargement des utilisateurs…</p>
      } @else if (state() === 'error') {
        <p role="alert">Impossible de charger les utilisateurs.</p>
        <button matButton="text" (click)="refresh()">Réessayer</button>
      } @else {
        <app-access-list
          [rows]="filteredRows()"
          [actions]="actions"
          (rowAction)="onAction($event)"
          emptyMessage="Aucun utilisateur."
        />
      }
    </app-configuration-section>
    <ng-template #userEditor
      ><app-record-form
        [data]="form()"
        [busy]="busy()"
        (submitted)="submit($event)"
        (cancelled)="editor.close()"
    /></ng-template>
  `,
})
export class UserManagementTabComponent {
  protected readonly editor = inject(SettingsEditor);
  private readonly userEditor = viewChild.required<TemplateRef<unknown>>('userEditor');
  private readonly recordForm = viewChild(RecordFormComponent);
  private readonly accounts = inject(UserAccounts);
  private readonly dialog = inject(MatDialog);
  private readonly auth = inject(AuthService);
  private readonly notifications = inject(NotificationService);

  readonly usersUpdated = output<readonly UserAccount[]>();
  private readonly users = signal<readonly UserAccount[]>([]);
  protected readonly state = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly query = signal('');
  protected readonly busy = signal(false);

  protected readonly actions: readonly AccessListAction<UserRow>[] = [
    {
      id: 'make-admin',
      label: 'Passer administrateur',
      visibleWhen: (row) => row.role === 'viewer',
    },
    {
      id: 'make-viewer',
      label: 'Passer en lecture seule',
      visibleWhen: (row) => row.role === 'admin' && !row.isSelf,
      confirmation: (row) => ({
        title: 'Retirer les droits d’administration ?',
        message: `« ${row.username} » pourra consulter le cluster sans pouvoir le modifier.`,
        confirmLabel: 'Passer en lecture seule',
      }),
    },
    {
      id: 'deactivate',
      label: 'Désactiver',
      visibleWhen: (row) => !!row.isActive && !row.isSelf,
      confirmation: (row) => ({
        title: 'Désactiver ce compte ?',
        message: `Les sessions de « ${row.username} » seront fermées immédiatement.`,
        confirmLabel: 'Désactiver',
      }),
    },
    { id: 'activate', label: 'Réactiver', visibleWhen: (row) => !row.isActive },
    { id: 'own-password', label: 'Changer mon mot de passe', visibleWhen: (row) => row.isSelf },
    { id: 'password', label: 'Réinitialiser le mot de passe', visibleWhen: (row) => !row.isSelf },
    {
      id: 'delete',
      label: 'Supprimer',
      tone: 'danger',
      visibleWhen: (row) => !row.isSelf,
      confirmation: (row) => ({
        title: 'Supprimer ce compte ?',
        message: `« ${row.username} » ne pourra plus se connecter. Cette action est définitive.`,
        confirmLabel: 'Supprimer',
      }),
    },
  ];

  protected readonly rows = computed<readonly UserRow[]>(() =>
    this.users().map((user) => ({
      id: user.id,
      title: user.username,
      kind: 'user' as const,
      username: user.username,
      role: user.role,
      isActive: user.isActive,
      isSelf: user.id === this.auth.user()?.id,
      details: [`Créé le ${new Date(user.createdAt).toLocaleDateString('fr-FR')}`],
      badges: [
        { label: USER_ROLE_LABELS[user.role], tone: 'accent' as const, plain: true },
        user.isActive
          ? { label: 'Actif', tone: 'success' as const }
          : { label: 'Désactivé', tone: 'neutral' as const },
      ],
    })),
  );

  protected readonly filteredRows = computed(() => {
    const q = this.query().trim().toLowerCase();
    return this.rows().filter((row) => !q || row.username.toLowerCase().includes(q));
  });

  protected readonly form = computed<RecordFormData>(() => ({
    title: 'Nouvel utilisateur',
    description: 'Créez un compte pour un membre de l’équipe.',
    review: false,
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
        minLength: MINIMUM_PASSWORD_LENGTH,
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
  }));

  constructor() {
    void this.refresh();
  }

  async refresh(): Promise<void> {
    try {
      const list = await firstValueFrom(this.accounts.list());
      this.users.set(list);
      this.usersUpdated.emit(list);
      this.state.set('ready');
    } catch {
      this.state.set(this.users().length ? 'ready' : 'error');
    }
  }

  protected newUser(): void {
    void this.editor.open({
      title: 'Créer un compte',
      content: this.userEditor(),
      dirty: () => this.recordForm()?.hasUnsavedChanges() ?? false,
      busy: () => this.busy(),
    });
  }

  protected closeForm(): void {
    this.editor.reset();
  }

  protected async onAction(event: AccessListActionEvent<UserRow>): Promise<void> {
    const row = event.row;
    switch (event.actionId) {
      // Même fenêtre, mêmes règles et mêmes messages que « Changer le mot de passe » du menu du compte.
      case 'own-password':
        this.dialog.open(PasswordChangeDialogComponent);
        return;
      case 'password':
        this.dialog.open(PasswordChangeDialogComponent, {
          data: { target: { id: row.id, username: row.username } },
        });
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
    this.busy.set(true);
    try {
      await firstValueFrom(
        this.accounts.create(
          value['username'].trim(),
          value['password'],
          value['role'] as 'admin' | 'viewer',
        ),
      );
      this.notifications.show(`« ${value['username'].trim()} » a été créé.`);
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
