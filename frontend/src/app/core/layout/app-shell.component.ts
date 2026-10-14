import { PageTitle } from '../../shared/services/page-title';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../../shared/services/cluster-overview';
import { Component, computed, inject } from '@angular/core';
import { toSignal, toObservable } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { filter, map, of, switchMap } from 'rxjs';
import { PrimaryNavigationComponent } from '../navigation/primary-navigation.component';
import { AuthService } from '../auth/auth.service';
import { UserMenuComponent } from '../auth/user-menu.component';

@Component({
  selector: 'app-shell',
  imports: [
    MatToolbarModule,
    PrimaryNavigationComponent,
    RouterLink,
    RouterOutlet,
    UserMenuComponent,
  ],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.scss',
})
export class AppShellComponent {
  private readonly router = inject(Router);
  private readonly navigationEnd = toSignal(
    this.router.events.pipe(
      filter((event): event is NavigationEnd => event instanceof NavigationEnd),
      map(() => this.getCurrentPageContext()),
    ),
    { initialValue: this.getCurrentPageContext() },
  );

  private readonly dynamicTitle = inject(PageTitle);
  protected readonly pageTitle = computed(() => {
    const context = this.navigationEnd();
    const override = this.dynamicTitle.current();
    return override?.url === context.url ? override.title : context.title;
  });
  protected readonly workerMode = computed(() => this.navigationEnd().workerMode);
  protected readonly workspaceMode = computed(() => this.navigationEnd().workspaceMode);
  protected readonly authMode = computed(() => this.navigationEnd().authMode);

  private readonly auth = inject(AuthService);
  // Aucun appel à l’API de gestion sans session : console worker, pages d’authentification et visiteur anonyme.
  private readonly pollingDisabled = computed(
    () => this.workerMode() || this.authMode() || !this.auth.user(),
  );
  private readonly overview = inject(ClusterOverview);
  private readonly clusterState = toSignal(
    toObservable(this.pollingDisabled).pipe(
      switchMap((disabled) => (disabled ? of(INITIAL_CLUSTER_OVERVIEW) : this.overview.state$)),
    ),
    { initialValue: INITIAL_CLUSTER_OVERVIEW },
  );
  protected readonly clusterLabel = computed(() => {
    const state = this.clusterState();
    if (state.status === 'loading') return 'Chargement…';
    if (state.status === 'error') return 'Données indisponibles';
    const nodes = state.snapshot?.nodes ?? [];
    return nodes.filter((n) => n.status === 'online').length + ' / ' + nodes.length + ' en ligne';
  });

  private getCurrentPageContext(): {
    title: string;
    url: string;
    workerMode: boolean;
    workspaceMode: boolean;
    authMode: boolean;
  } {
    let route = this.router.routerState.snapshot.root;

    while (route.firstChild) {
      route = route.firstChild;
    }

    return {
      url: this.router.url,
      title: (route.data['title'] as string | undefined) ?? 'Vue d’ensemble',
      workerMode: route.data['workerMode'] === true,
      workspaceMode: route.data['layout'] === 'workspace',
      authMode: route.data['authPage'] !== undefined,
    };
  }
}
