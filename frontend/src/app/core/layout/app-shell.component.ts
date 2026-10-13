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
      map(() => this.getCurrentPageTitle()),
    ),
    { initialValue: this.getCurrentPageTitle() },
  );

  protected readonly pageTitle = computed(() => this.navigationEnd());

  private getCurrentPageTitle(): string {
    let route = this.router.routerState.snapshot.root;

    while (route.firstChild) {
      route = route.firstChild;
    }

    return (route.data['title'] as string | undefined) ?? 'Vue d’ensemble';
  }
}
