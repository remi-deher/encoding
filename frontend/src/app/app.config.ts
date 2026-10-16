import { ApplicationConfig, inject, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { authInterceptor } from './core/auth/auth.interceptor';
import {
  provideRouter,
  Router,
  withComponentInputBinding,
  withViewTransitions,
} from '@angular/router';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideRouter(
      routes,
      withComponentInputBinding(),
      withViewTransitions({
        skipInitialTransition: true,
        onViewTransitionCreated: ({ transition }) => {
          const router = inject(Router);
          const direction = (
            router.currentNavigation()?.extras.info as
              { readonly flowEditorDirection?: 'forward' | 'backward' } | undefined
          )?.flowEditorDirection;
          if (direction) {
            const transitionTypes = (
              transition as ViewTransition & { readonly types?: { add(type: string): void } }
            ).types;
            transitionTypes?.add(`flow-editor-${direction}`);
          }
        },
      }),
    ),
  ],
};
