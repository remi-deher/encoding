import {
  ExecutionTracking,
  INITIAL_EXECUTION_TRACKING,
} from '../../shared/services/execution-tracking';
import { PageTitle } from '../../shared/services/page-title';
import { ClusterOverview, INITIAL_CLUSTER_OVERVIEW } from '../../shared/services/cluster-overview';
import { Component, computed, inject } from '@angular/core';
import { toSignal, toObservable } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { filter, map, of, switchMap } from 'rxjs';
import { PrimaryNavigationComponent } from '../navigation/primary-navigation.component';

@Component({
  selector: 'app-shell',
  imports: [MatToolbarModule, PrimaryNavigationComponent, RouterLink, RouterOutlet],
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

  private readonly overview = inject(ClusterOverview);
  private readonly clusterState = toSignal(
    toObservable(this.workerMode).pipe(
      switchMap((worker) => (worker ? of(INITIAL_CLUSTER_OVERVIEW) : this.overview.state$)),
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

  private readonly tracking = inject(ExecutionTracking);
  private readonly executionState = toSignal(
    toObservable(this.workerMode).pipe(
      switchMap((worker) => (worker ? of(INITIAL_EXECUTION_TRACKING) : this.tracking.state$)),
    ),
    { initialValue: INITIAL_EXECUTION_TRACKING },
  );
  protected readonly activeExecutionCount = computed(
    () => this.executionState().data?.activeCount ?? 0,
  );

  private getCurrentPageContext(): {
    title: string;
    url: string;
    workerMode: boolean;
    workspaceMode: boolean;
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
    };
  }
}
