import { Component, computed, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatMenuModule } from '@angular/material/menu';
import { USER_ROLE_LABELS } from '../../shared/models/auth';
import { PasswordChangeDialogComponent } from './password-change-dialog.component';
import { AuthService } from './auth.service';

@Component({
  selector: 'app-user-menu',
  imports: [MatButtonModule, MatMenuModule],
  template: `
    @if (user(); as current) {
      <button
        matButton="text"
        type="button"
        class="user-trigger"
        [matMenuTriggerFor]="menu"
        [attr.aria-label]="'Compte de ' + current.username + ', ' + roleLabel()"
      >
        <span class="user-avatar" aria-hidden="true">{{
          current.username.slice(0, 1).toUpperCase()
        }}</span>
        <span class="user-name">{{ current.username }}</span>
        <span class="user-role">{{ roleLabel() }}</span>
      </button>
      <mat-menu #menu="matMenu" xPosition="before">
        <button mat-menu-item type="button" (click)="changePassword()">
          Changer le mot de passe
        </button>
        <button mat-menu-item type="button" (click)="logout()">Se déconnecter</button>
      </mat-menu>
    }
  `,
  styles: `
    .user-trigger {
      display: inline-flex;
      align-items: center;
      gap: var(--space-2);
      font-size: var(--font-body);
    }
    .user-avatar {
      width: 28px;
      height: 28px;
      display: none;
      align-items: center;
      justify-content: center;
      border-radius: var(--radius-pill);
      background: var(--accent-soft);
      color: var(--accent-strong);
      font-size: var(--font-label);
      font-weight: 700;
    }
    .user-name {
      font-weight: 650;
      color: var(--text-primary);
    }
    .user-role {
      padding: 3px var(--space-2);
      border: 1px solid var(--border-subtle);
      border-radius: var(--radius-pill);
      color: var(--text-muted);
      background: var(--surface-muted);
      font-size: var(--font-body);
    }
    @media (max-width: 760px) {
      .user-trigger {
        width: 36px;
        min-width: 36px;
        height: 36px;
        padding: 0;
        justify-content: center;
      }
      .user-avatar {
        display: inline-flex;
      }
      .user-name,
      .user-role {
        display: none;
      }
    }
  `,
})
export class UserMenuComponent {
  private readonly auth = inject(AuthService);
  private readonly dialog = inject(MatDialog);
  protected readonly user = this.auth.user;
  protected readonly roleLabel = computed(() => {
    const role = this.user()?.role;
    return role ? USER_ROLE_LABELS[role] : '';
  });

  protected changePassword(): void {
    this.dialog.open(PasswordChangeDialogComponent, { width: '420px', maxWidth: '95vw' });
  }

  protected logout(): void {
    void this.auth.logout();
  }
}
