import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject, Injector } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { isAuthEndpoint, isStateChangingMethod } from '../../shared/models/auth';
import { NotificationService } from '../../shared/services/notification.service';
import { AuthService } from './auth.service';

/** En-tête exigé par le serveur pour toute modification authentifiée par cookie (protection inter-sites). */
export const CSRF_HEADER = 'X-Encoding-Csrf';

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  if (!request.url.startsWith('/api/')) return next(request);
  const injector = inject(Injector);
  const outgoing = isStateChangingMethod(request.method)
    ? request.clone({ setHeaders: { [CSRF_HEADER]: '1' } })
    : request;
  return next(outgoing).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && !isAuthEndpoint(request.url)) {
        if (error.status === 401) injector.get(AuthService).sessionExpired();
        else if (error.status === 403)
          injector.get(NotificationService).show('Cette action est réservée aux administrateurs.');
      }
      return throwError(() => error);
    }),
  );
};
