import { Component, inject, signal } from '@angular/core';
import { MatDialogContent, MatDialogRef, MatDialogTitle } from '@angular/material/dialog';
import { MINIMUM_PASSWORD_LENGTH } from '../../shared/models/auth';
import { NotificationService } from '../../shared/services/notification.service';
import { RecordFormComponent, RecordFormData } from '../../shared/ui/record-form.component';
import { AuthService } from './auth.service';

@Component({
  selector: 'app-password-change-dialog',
  imports: [MatDialogContent, MatDialogTitle, RecordFormComponent],
  template: `
    <section class="app-dialog">
      <header class="app-dialog-header">
        <span class="app-dialog-icon" aria-hidden="true">◆</span>
        <div class="app-dialog-heading">
          <p class="app-dialog-eyebrow">Sécurité du compte</p>
          <h2 mat-dialog-title>Changer le mot de passe</h2>
        </div>
      </header>
      <mat-dialog-content class="app-dialog-body">
        <app-record-form
          [data]="form"
          [busy]="busy()"
          (submitted)="save($event)"
          (cancelled)="dialogRef.close()"
        />
      </mat-dialog-content>
    </section>
  `,
})
export class PasswordChangeDialogComponent {
  protected readonly dialogRef = inject(MatDialogRef<PasswordChangeDialogComponent>);
  private readonly notifications = inject(NotificationService);
  private readonly auth = inject(AuthService);
  protected readonly busy = signal(false);
  protected readonly form: RecordFormData = {
    title: 'Changer le mot de passe',
    description: '',
    submitLabel: 'Changer le mot de passe',
    review: false,
    fields: [
      {
        id: 'currentPassword',
        label: 'Mot de passe actuel',
        type: 'password',
        autocomplete: 'current-password',
        required: true,
      },
      {
        id: 'newPassword',
        label: 'Nouveau mot de passe',
        type: 'password',
        autocomplete: 'new-password',
        required: true,
        hint: `Au moins ${MINIMUM_PASSWORD_LENGTH} caractères. Vos autres sessions seront fermées.`,
      },
    ],
  };

  constructor() {
    this.dialogRef.addPanelClass('app-dialog-panel');
    this.dialogRef.updateSize('480px');
  }

  protected async save(value: Record<string, string>): Promise<void> {
    this.busy.set(true);
    try {
      await this.auth.changePassword(value['currentPassword'], value['newPassword']);
      this.notifications.show('Votre mot de passe a été modifié.');
      this.dialogRef.close();
    } catch (error) {
      this.notifications.show(
        AuthService.errorMessage(error, 'Le mot de passe n’a pas pu être modifié.'),
      );
    } finally {
      this.busy.set(false);
    }
  }
}
