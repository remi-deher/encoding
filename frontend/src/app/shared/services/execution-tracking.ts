import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import {
  catchError,
  exhaustMap,
  map,
  merge,
  of,
  scan,
  shareReplay,
  Subject,
  timeout,
  timer,
} from 'rxjs';
import { ExecutionOverviewResponse, ExecutionEventResponse } from '../models/execution';

export interface ExecutionTrackingState {
  readonly status: 'loading' | 'ready' | 'error';
  readonly data: ExecutionOverviewResponse | null;
}
export const INITIAL_EXECUTION_TRACKING: ExecutionTrackingState = { status: 'loading', data: null };
@Injectable({ providedIn: 'root' })
export class ExecutionTracking {
  private readonly http = inject(HttpClient);
  private readonly refreshRequested = new Subject<void>();
  readonly state$ = merge(timer(0, 5000), this.refreshRequested).pipe(
    exhaustMap(() =>
      this.http.get<ExecutionOverviewResponse>('/api/v1/executions').pipe(
        timeout(8000),
        map((data) => ({ status: 'ready', data }) as ExecutionTrackingState),
        catchError(() => of({ status: 'error', data: null } as ExecutionTrackingState)),
      ),
    ),
    scan(
      (previous: ExecutionTrackingState, next: ExecutionTrackingState) =>
        next.status === 'error' ? { ...next, data: previous.data } : next,
      INITIAL_EXECUTION_TRACKING,
    ),
    shareReplay({ bufferSize: 1, refCount: true }),
  );
  events(id: string) {
    return this.http.get<readonly ExecutionEventResponse[]>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/events',
    );
  }
  cancel(id: string) {
    return this.http.post<void>('/api/v1/executions/' + encodeURIComponent(id) + '/cancel', {});
  }
  retry(id: string) {
    return this.http.post<{ id: string }>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/retry',
      {},
    );
  }
  refresh() {
    this.refreshRequested.next();
  }
}
