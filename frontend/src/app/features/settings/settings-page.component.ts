import {
  ChangeDetectionStrategy,
  Component,
  effect,
  inject,
  signal,
} from '@angular/core';
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

export type SettingsMainTab = 'security' | 'integrations' | 'system';
export type SecuritySubTab = 'tokens' | 'users';
export type IntegrationsSubTab = 'mcp' | 'webhooks' | 'ai';

@Component({
  selector: 'app-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    AiToolsTabComponent,
    ApiTokenTabComponent,
    CategoryTabsComponent,
    ManagementCatalogTemplateComponent,
    McpServerTabComponent,
    SystemTabComponent,
    UserManagementTabComponent,
    WebhooksTabComponent,
  ],
  template: `
    <app-management-catalog-template
      title="Paramètres"
      [showToolbar]="false"
    >
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
                [class.is-active]="securityTab() === 'tokens'"
                (click)="onSubTabChange('tokens')"
              >
                Jetons d’API
              </button>
              <button
                type="button"
                class="subtab-btn"
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
                [class.is-active]="integrationsTab() === 'mcp'"
                (click)="onSubTabChange('mcp')"
              >
                Serveur MCP
              </button>
              <button
                type="button"
                class="subtab-btn"
                [class.is-active]="integrationsTab() === 'ai'"
                (click)="onSubTabChange('ai')"
              >
                Outils IA & CLIs
              </button>
              <button
                type="button"
                class="subtab-btn"
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
            <app-system-tab />
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
      background: var(--primary);
      color: var(--on-primary, #ffffff);
      font-weight: 600;
    }
  `,
})
export class SettingsPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly activeMainTab = signal<SettingsMainTab>('security');
  protected readonly securityTab = signal<SecuritySubTab>('tokens');
  protected readonly integrationsTab = signal<IntegrationsSubTab>('mcp');
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
    } else if (tab === 'system') {
      this.activeMainTab.set('system');
    }
  }

  protected onMainTabChange(tab: string): void {
    if (tab === 'security' || tab === 'integrations' || tab === 'system') {
      this.activeMainTab.set(tab);
      const sub = tab === 'security' ? this.securityTab() : tab === 'integrations' ? this.integrationsTab() : 'system';
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { tab: sub },
        queryParamsHandling: 'merge',
      });
    }
  }

  protected onSubTabChange(subTab: SecuritySubTab | IntegrationsSubTab): void {
    if (subTab === 'tokens' || subTab === 'users') {
      this.securityTab.set(subTab);
    } else if (subTab === 'mcp' || subTab === 'webhooks') {
      this.integrationsTab.set(subTab);
    }
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { tab: subTab },
      queryParamsHandling: 'merge',
    });
  }
}
