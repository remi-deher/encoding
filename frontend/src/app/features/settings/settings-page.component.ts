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

export type SettingsTabId = 'users' | 'tokens' | 'mcp' | 'system' | 'webhooks';

@Component({
  selector: 'app-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
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
      note="Configuration du cluster, des comptes utilisateurs, des jetons d’API pour les agents IA et du serveur Model Context Protocol (MCP)."
    >
      <app-category-tabs
        catalogue-navigation
        label="Sections de configuration"
        [options]="tabs"
        [selected]="activeTab()"
        (selectedChange)="onTabChange($event)"
      />

      <div catalogue-table class="settings-content-wrapper">
        @switch (activeTab()) {
          @case ('users') {
            <app-user-management-tab />
          }
          @case ('tokens') {
            <app-api-token-tab (tokensUpdated)="cachedTokens.set($event)" />
          }
          @case ('mcp') {
            <app-mcp-server-tab [tokens]="cachedTokens()" />
          }
          @case ('system') {
            <app-system-tab />
          }
          @case ('webhooks') {
            <app-webhooks-tab />
          }
        }
      </div>
    </app-management-catalog-template>
  `,
  styles: `
    .settings-content-wrapper {
      margin-top: var(--space-2);
    }
  `,
})
export class SettingsPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  protected readonly activeTab = signal<SettingsTabId>('users');
  protected readonly cachedTokens = signal<readonly ApiTokenResponse[]>([]);

  protected readonly tabs: readonly CategoryTabOption[] = [
    { value: 'users', label: 'Utilisateurs' },
    { value: 'tokens', label: 'Jetons d’API (IA)' },
    { value: 'mcp', label: 'Serveur MCP' },
    { value: 'system', label: 'Système & Santé' },
    { value: 'webhooks', label: 'Webhooks & Intégrations' },
  ];

  constructor() {
    const initialTab = this.route.snapshot.queryParamMap.get('tab');
    if (this.isValidTab(initialTab)) {
      this.activeTab.set(initialTab);
    }

    effect(() => {
      const queryTab = this.route.snapshot.queryParamMap.get('tab');
      if (this.isValidTab(queryTab) && queryTab !== this.activeTab()) {
        this.activeTab.set(queryTab);
      }
    });
  }

  protected onTabChange(tab: string): void {
    if (this.isValidTab(tab)) {
      this.activeTab.set(tab);
      void this.router.navigate([], {
        relativeTo: this.route,
        queryParams: { tab },
        queryParamsHandling: 'merge',
      });
    }
  }

  private isValidTab(tab: string | null): tab is SettingsTabId {
    return (
      tab === 'users' ||
      tab === 'tokens' ||
      tab === 'mcp' ||
      tab === 'system' ||
      tab === 'webhooks'
    );
  }
}
