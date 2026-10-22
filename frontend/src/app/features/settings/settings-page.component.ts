import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { PageHeaderComponent } from '../../shared/ui/page-header.component';
import { DetailDrawerComponent } from '../../shared/ui/detail-drawer/detail-drawer';
import { ConfigurationSectionComponent } from '../../shared/ui/configuration-section.component';
import { SystemLogViewerComponent } from '../../shared/ui/system-log-viewer/system-log-viewer.component';
import { UserAccount } from '../../shared/models/auth';
import { ApiTokenResponse } from '../../shared/models/api-token';
import { AccessSummaryComponent } from './tabs/access-summary.component';
import { ApiTokenTabComponent } from './tabs/api-token-tab.component';
import { UserManagementTabComponent } from './tabs/user-management-tab.component';
import { AiToolsTabComponent } from './tabs/ai-tools-tab.component';
import { McpServerTabComponent } from './tabs/mcp-server-tab.component';
import { WebhooksTabComponent } from './tabs/webhooks-tab.component';
import { MetricsTabComponent } from './tabs/metrics-tab.component';
import { SystemTabComponent } from './tabs/system-tab.component';
import { NetworkTabComponent } from './tabs/network-tab.component';
import { AppearanceTabComponent } from './tabs/appearance-tab.component';
import { SettingsEditor } from './settings-editor';

export type SettingsMainTab =
  'interface' | 'access' | 'ai' | 'connections' | 'network' | 'operations';
export function settingsDomain(value: string | null): SettingsMainTab {
  if (['access', 'security', 'tokens', 'users'].includes(value ?? '')) return 'access';
  if (['ai', 'mcp', 'assistant'].includes(value ?? '')) return 'ai';
  if (['connections', 'integrations', 'webhooks', 'metrics'].includes(value ?? ''))
    return 'connections';
  if (['network', 'proxy', 'reverse-proxy'].includes(value ?? '')) return 'network';
  if (['operations', 'system', 'overview', 'logs', 'system-logs'].includes(value ?? ''))
    return 'operations';
  return 'interface';
}
@Component({
  selector: 'app-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  providers: [SettingsEditor],
  imports: [
    PageHeaderComponent,
    CategoryTabsComponent,
    DetailDrawerComponent,
    ConfigurationSectionComponent,
    AccessSummaryComponent,
    ApiTokenTabComponent,
    UserManagementTabComponent,
    AiToolsTabComponent,
    McpServerTabComponent,
    WebhooksTabComponent,
    MetricsTabComponent,
    SystemTabComponent,
    AppearanceTabComponent,
    NetworkTabComponent,
    SystemLogViewerComponent,
  ],
  template: `
    <app-detail-drawer
      [opened]="!!editor.state()"
      [title]="editor.state()?.title ?? ''"
      [content]="editor.state()?.content"
      size="standard"
      [modal]="true"
      mainScroll="parent"
      [dismissible]="!editor.isBusy() && !editor.hasUnsavedChanges()"
      (closed)="editor.drawerClosed()"
    >
      <div drawer-main>
        <app-page-header title="Paramètres" [description]="descriptions[activeMainTab()]" />
        <app-category-tabs
          label="Domaines des paramètres"
          [options]="mainTabs"
          [selected]="activeMainTab()"
          variant="underline"
          (selectedChange)="onMainTabChange($event)"
        />
        <div
          class="settings-content"
          role="tabpanel"
          [attr.aria-label]="descriptions[activeMainTab()]"
        >
          @switch (activeMainTab()) {
            @case ('interface') {
              <app-appearance-tab />
            }
            @case ('access') {
              <app-access-summary [users]="users()" [tokens]="tokens()" />
              <app-user-management-tab (usersUpdated)="users.set($event)" />
              <app-api-token-tab (tokensUpdated)="tokens.set($event)" />
            }
            @case ('ai') {
              <app-ai-tools-tab /><app-mcp-server-tab />
            }
            @case ('connections') {
              <app-webhooks-tab /><app-metrics-tab />
            }
            @case ('network') {
              <app-network-tab />
            }
            @case ('operations') {
              <app-system-tab />
              <app-configuration-section
                title="Journaux système"
                description="Événements des services sur le nœud consulté."
                ><app-system-log-viewer
              /></app-configuration-section>
            }
          }
        </div>
      </div>
    </app-detail-drawer>
  `,
  styles: `
    .settings-content {
      display: grid;
      gap: var(--space-6);
      margin-top: var(--space-6);
      max-width: 1160px;
      min-width: 0;
    }
  `,
})
export class SettingsPageComponent {
  private navigationApproved = false;
  private readonly aiTab = viewChild(AiToolsTabComponent);
  protected readonly editor = inject(SettingsEditor);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly activeMainTab = signal<SettingsMainTab>('interface');
  protected readonly users = signal<readonly UserAccount[]>([]);
  protected readonly tokens = signal<readonly ApiTokenResponse[]>([]);
  protected readonly descriptions: Record<SettingsMainTab, string> = {
    interface: 'Vos préférences pour cette interface.',
    access: 'Les comptes et les accès des applications à votre cluster.',
    ai: 'Le moteur, les fournisseurs, la consommation et les assistants externes.',
    connections: 'Les applications qui s’adressent à votre cluster et la supervision.',
    network: 'L’accès à ce nœud derrière un reverse proxy.',
    operations: 'La santé des services et les événements de ce nœud.',
  };
  protected readonly mainTabs: readonly CategoryTabOption[] = [
    { value: 'interface', label: 'Interface' },
    { value: 'access', label: 'Accès' },
    { value: 'ai', label: 'IA' },
    { value: 'connections', label: 'Connexions' },
    { value: 'network', label: 'Réseau' },
    { value: 'operations', label: 'Exploitation' },
  ];
  constructor() {
    this.route.queryParamMap.pipe(takeUntilDestroyed()).subscribe((params) => {
      const domain = settingsDomain(params.get('tab'));
      if (domain !== this.activeMainTab()) this.editor.reset();
      this.activeMainTab.set(domain);
    });
    this.destroyRef.onDestroy(() => this.editor.reset());
  }
  hasUnsavedChanges(): boolean {
    return (
      !this.navigationApproved &&
      (this.editor.hasUnsavedChanges() || (this.aiTab()?.hasUnsavedChanges() ?? false))
    );
  }
  protected async onMainTabChange(value: string): Promise<void> {
    const tab = settingsDomain(value);
    if (tab === this.activeMainTab() || this.editor.isBusy()) return;
    if (this.hasUnsavedChanges() && !(await this.editor.confirmDiscard())) return;
    this.navigationApproved = true;
    try {
      if (
        await this.router.navigate([], {
          relativeTo: this.route,
          queryParams: { tab },
          queryParamsHandling: 'merge',
        })
      )
        this.editor.reset();
    } finally {
      this.navigationApproved = false;
    }
  }
}
