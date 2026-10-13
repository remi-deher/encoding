import { ApplicationConfig, inject, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideRouter, Router, withViewTransitions } from '@angular/router';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(),
    provideRouter(
      routes,
      withViewTransitions({
        skipInitialTransition: true,
        onViewTransitionCreated: ({ transition }) => {
          const router = inject(Router);
          const direction = (router.currentNavigation()?.extras.info as
            | { readonly flowEditorDirection?: 'forward' | 'backward' }
            | undefined)?.flowEditorDirection;
          if (!direction) {
            transition.skipTransition();
            return;
          }

          const transitionTypes = (
            transition as ViewTransition & { readonly types?: { add(type: string): void } }
          ).types;
          transitionTypes?.add(`flow-editor-${direction}`);
        },
      }),
    ),
  ],
};
