import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { firstValueFrom } from 'rxjs';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiTokenResponse } from '../../shared/models/api-token';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { ApiTokenTabComponent } from './tabs/api-token-tab.component';
import { McpServerTabComponent } from './tabs/mcp-server-tab.component';
import { SystemTabComponent } from './tabs/system-tab.component';
import { UserManagementTabComponent } from './tabs/user-management-tab.component';
import { WebhooksTabComponent } from './tabs/webhooks-tab.component';
import { AiToolsTabComponent } from './tabs/ai-tools-tab.component';

import { SystemLogViewerComponent } from '../../shared/ui/system-log-viewer/system-log-viewer.component';

export type SettingsMainTab = 'security' | 'integrations' | 'system';
export type SecuritySubTab = 'tokens' | 'users';
export type IntegrationsSubTab = 'mcp' | 'webhooks' | 'ai';
export type SystemSubTab = 'overview' | 'logs';

@Component({
  selector: 'app-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    AiToolsTabComponent,
    ApiTokenTabComponent,
    CategoryTabsComponent,
    ManagementCatalogTemplateComponent,
    McpServerTabComponent,
    SystemLogViewerComponent,
    SystemTabComponent,
    UserManagementTabComponent,
    WebhooksTabComponent,
  ],
  template: `
    <app-management-catalog-template title="Paramètres" [showToolbar]="false">
      <app-category-tabs
        catalogue-navigation
        label="Domaines de configuration"
        [options]="mainTabs"
        [selected]="activeMainTab()"
        (selectedChange)="onMainTabChange($event)"
      />

      <div catalogue-table class="settings-content-wrapper">
        @switch (activeMainTab()) {
          @case ('security') {
            <div class="subtab-bar">
              <button
                type="button"
                class="subtab-btn"
                matButton="text"
                [class.is-active]="securityTab() === 'tokens'"
                (click)="onSubTabChange('tokens')"
              >
                Jetons d’API
              </button>
              <button
                type="button"
                class="subtab-btn"
                matButton="text"
                [class.is-active]="securityTab() === 'users'"
                (click)="onSubTabChange('users')"
              >
                Comptes utilisateurs
              </button>
            </div>

            @if (securityTab() === 'tokens') {
              <app-api-token-tab (tokensUpdated)="cachedTokens.set($event)" />
            } @else {
              <app-user-management-tab />
            }
          }

          @case ('integrations') {
            <div class="subtab-bar">
              <button
                type="button"
                class="subtab-btn"
                matButton="text"
                [class.is-active]="integrationsTab() === 'mcp'"
                (click)="onSubTabChange('mcp')"
              >
                Serveur MCP
              </button>
              <button
                type="button"
                class="subtab-btn"
                matButton="text"
                [class.is-active]="integrationsTab() === 'ai'"
                (click)="onSubTabChange('ai')"
              >
                IA & fournisseurs
              </button>
              <button
                type="button"
                class="subtab-btn"
                matButton="text"
                [class.is-active]="integrationsTab() === 'webhooks'"
                (click)="onSubTabChange('webhooks')"
              >
                Webhooks & Métriques
              </button>
            </div>

            @if (integrationsTab() === 'mcp') {
              <app-mcp-server-tab [tokens]="cachedTokens()" />
            } @else if (integrationsTab() === 'ai') {
              <app-ai-tools-tab />
            } @else {
              <app-webhooks-tab />
            }
          }

          @case ('system') {
            <div class="subtab-bar">
              <button
                type="button"
                class="subtab-btn"
                matButton="text"
                [class.is-active]="systemTab() === 'overview'"
                (click)="onSubTabChange('overview')"
              >
                Santé & Apparence
              </button>
              <button
                type="button"
                class="subtab-btn"
                matButton="text"
                [class.is-active]="systemTab() === 'logs'"
                (click)="onSubTabChange('logs')"
              >
                Journaux système
              </button>
            </div>

            @if (systemTab() === 'overview') {
              <app-system-tab />
            } @else {
              <app-system-log-viewer />
            }
          }
        }
      </div>
    </app-management-catalog-template>
  `,
  styles: `
    .settings-content-wrapper {
      margin-top: var(--space-2);
      display: grid;
      gap: var(--space-3);
    }

    .subtab-bar {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      padding-bottom: var(--space-2);
      border-bottom: 1px solid var(--outline-variant);
    }

    .subtab-btn {
      padding: var(--space-2) var(--space-4);
      border-radius: var(--radius-pill);
      border: none;
      background: var(--surface-container-high);
      color: var(--text-secondary);
      font-size: var(--font-label);
      font-weight: 500;
      cursor: pointer;
      transition: all 0.2s ease;
    }

    .subtab-btn:hover {
      background: var(--surface-container-highest);
      color: var(--text-primary);
    }

    .subtab-btn.is-active {
      background: var(--accent-soft);
      color: var(--accent-strong);
      font-weight: 600;
    }
  `,
})
export class SettingsPageComponent {
  private readonly aiTab = viewChild(AiToolsTabComponent);
  private readonly dialog = inject(MatDialog);
  hasUnsavedChanges(): boolean {
    return this.aiTab()?.hasUnsavedChanges() ?? false;
  }
  private async mayLeaveAi(): Promise<boolean> {
    if (!this.hasUnsavedChanges()) return true;
    return (
      (await firstValueFrom(
        this.dialog
          .open(ConfirmationDialogComponent, {
            data: {
              title: 'Quitter sans enregistrer ?',
              message: 'Les modifications des paramètres IA seront perdues.',
              confirmLabel: 'Quitter sans enregistrer',
            },
          })
          .afterClosed(),
      )) === true
    );
  }
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly activeMainTab = signal<SettingsMainTab>('security');
  protected readonly securityTab = signal<SecuritySubTab>('tokens');
  protected readonly integrationsTab = signal<IntegrationsSubTab>('mcp');
  protected readonly systemTab = signal<SystemSubTab>('overview');
  protected readonly cachedTokens = signal<readonly ApiTokenResponse[]>([]);

  protected readonly mainTabs: readonly CategoryTabOption[] = [
    { value: 'security', label: 'Accès & Sécurité' },
    { value: 'integrations', label: 'Intégrations & MCP' },
    { value: 'system', label: 'Système & Apparence' },
  ];

  constructor() {
    this.parseInitialRoute();

    effect(() => {
      const qp = this.route.snapshot.queryParamMap.get('tab');
      if (qp) this.applyTabParam(qp);
    });
  }

  private parseInitialRoute(): void {
    const qp = this.route.snapshot.queryParamMap.get('tab');
    if (qp) {
      this.applyTabParam(qp);
    }
  }

  private applyTabParam(tab: string): void {
    if (tab === 'tokens' || tab === 'users') {
      this.activeMainTab.set('security');
      this.securityTab.set(tab);
    } else if (tab === 'mcp' || tab === 'webhooks' || tab === 'ai') {
      this.activeMainTab.set('integrations');
      this.integrationsTab.set(tab as IntegrationsSubTab);
    } else if (tab === 'system' || tab === 'overview') {
      this.activeMainTab.set('system');
      this.systemTab.set('overview');
    } else if (tab === 'logs' || tab === 'system-logs') {
      this.activeMainTab.set('system');
      this.systemTab.set('logs');
    }
  }

  protected async onMainTabChange(tab: string): Promise<void> {
    if (tab !== this.activeMainTab() && !(await this.mayLeaveAi())) return;
    if (tab === 'security' || tab === 'integrations' || tab === 'system') {
      this.activeMainTab.set(tab);
      const sub =
        tab === 'security'
          ? this.securityTab()
          : tab === 'integrations'
            ? this.integrationsTab()
            : this.systemTab();
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { tab: sub },
        queryParamsHandling: 'merge',
      });
    }
  }

  protected async onSubTabChange(
    subTab: SecuritySubTab | IntegrationsSubTab | SystemSubTab,
  ): Promise<void> {
    if (subTab !== 'ai' && !(await this.mayLeaveAi())) return;
    if (subTab === 'tokens' || subTab === 'users') {
      this.securityTab.set(subTab);
    } else if (subTab === 'mcp' || subTab === 'webhooks' || subTab === 'ai') {
      this.integrationsTab.set(subTab);
    } else if (subTab === 'overview' || subTab === 'logs') {
      this.systemTab.set(subTab);
    }
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: subTab },
      queryParamsHandling: 'merge',
    });
  }
}
