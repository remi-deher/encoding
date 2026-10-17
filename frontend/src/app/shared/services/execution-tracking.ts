import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import {
  catchError,
  exhaustMap,
  map,
  merge,
  Observable,
  of,
  scan,
  shareReplay,
  Subject,
  timeout,
  timer,
} from 'rxjs';
import {
  ExecutionOverviewResponse,
  ExecutionEventResponse,
  ExecutionPipelineStep,
  ExecutionFilterQuery,
} from '../models/execution';

export interface ExecutionTrackingState {
  readonly status: 'loading' | 'ready' | 'error';
  readonly data: ExecutionOverviewResponse | null;
}
export const INITIAL_EXECUTION_TRACKING: ExecutionTrackingState = { status: 'loading', data: null };

@Injectable({ providedIn: 'root' })
export class ExecutionTracking {
  private readonly http = inject(HttpClient);
  private readonly refreshRequested = new Subject<void>();
  private readonly sseUpdates$ = new Observable<ExecutionTrackingState>((subscriber) => {
    if (typeof EventSource === 'undefined') {
      subscriber.complete();
      return undefined;
    }
    let source: EventSource | null = null;
    try {
      source = new EventSource('/api/v1/executions/stream', { withCredentials: true });
      source.addEventListener('overview', (event: MessageEvent<string>) => {
        try {
          const data = JSON.parse(event.data) as ExecutionOverviewResponse;
          subscriber.next({ status: 'ready', data });
        } catch {
          // Ignore malformed event payloads; polling remains active as fallback.
        }
      });
      source.onerror = () => {
        source?.close();
        source = null;
      };
    } catch {
      source?.close();
      source = null;
    }
    return () => {
      source?.close();
    };
  });

  readonly state$ = merge(
    merge(timer(0, 5000), this.refreshRequested).pipe(
      exhaustMap(() =>
        this.overview().pipe(
          timeout(8000),
          map((data) => ({ status: 'ready', data }) as ExecutionTrackingState),
          catchError(() => of({ status: 'error', data: null } as ExecutionTrackingState)),
        ),
      ),
    ),
    this.sseUpdates$,
  ).pipe(
    scan(
      (previous: ExecutionTrackingState, next: ExecutionTrackingState) =>
        next.status === 'error' ? { ...next, data: previous.data } : next,
      INITIAL_EXECUTION_TRACKING,
    ),
    shareReplay({ bufferSize: 1, refCount: true }),
  );

  overview(query?: ExecutionFilterQuery) {
    let params = new HttpParams();
    if (query?.state) params = params.set('state', query.state);
    if (query?.libraryId) params = params.set('libraryId', query.libraryId);
    if (query?.nodeId) params = params.set('nodeId', query.nodeId);
    if (query?.since) params = params.set('since', query.since);
    if (query?.limit !== undefined) params = params.set('limit', String(query.limit));
    if (query?.offset !== undefined) params = params.set('offset', String(query.offset));
    return this.http.get<ExecutionOverviewResponse>('/api/v1/executions', { params });
  }

  events(id: string, options?: { readonly afterRevision?: number; readonly limit?: number }) {
    let params = new HttpParams();
    if (options?.afterRevision !== undefined)
      params = params.set('afterRevision', String(options.afterRevision));
    if (options?.limit !== undefined) params = params.set('limit', String(options.limit));
    return this.http.get<readonly ExecutionEventResponse[]>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/events',
      { params },
    );
  }
  pipeline(id: string) {
    return this.http.get<readonly ExecutionPipelineStep[]>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/pipeline',
    );
  }
  artifacts(id: string) {
    return this.http.get<readonly { sha256: string; name: string; bytes: number }[]>(
      '/api/v1/executions/' + encodeURIComponent(id) + '/artifacts',
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
