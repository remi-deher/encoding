import { Component, computed, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterOutlet } from '@angular/router';
import { MatToolbarModule } from '@angular/material/toolbar';
import { filter, map } from 'rxjs';
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

  protected readonly pageTitle = computed(() => this.navigationEnd().title);
  protected readonly workerMode = computed(() => this.navigationEnd().workerMode);

  private getCurrentPageContext(): { title: string; workerMode: boolean } {
    let route = this.router.routerState.snapshot.root;

    while (route.firstChild) {
      route = route.firstChild;
    }

    return {
      title: (route.data['title'] as string | undefined) ?? 'Vue d’ensemble',
      workerMode: route.data['workerMode'] === true,
    };
  }
}
