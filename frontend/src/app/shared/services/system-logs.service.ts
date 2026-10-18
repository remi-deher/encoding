import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import {
  SystemLogCategoriesResponse,
  SystemLogEntry,
  SystemLogFilterQuery,
  SystemLogLevel,
  SystemLogOverviewResponse,
} from '../models/system-logs';

@Injectable({ providedIn: 'root' })
export class SystemLogsService {
  private readonly http = inject(HttpClient);

  getLogs(query: SystemLogFilterQuery = {}): Observable<SystemLogOverviewResponse> {
    let params = new HttpParams();
    if (query.minLevel) params = params.set('minLevel', query.minLevel);
    if (query.category) params = params.set('category', query.category);
    if (query.search) params = params.set('search', query.search);
    if (query.nodeId) params = params.set('nodeId', query.nodeId);
    if (query.limit !== undefined) params = params.set('limit', query.limit.toString());
    if (query.since) params = params.set('since', query.since);

    return this.http.get<SystemLogOverviewResponse>('/api/v1/system/logs', { params });
  }

  getCategories(nodeId?: string | null): Observable<readonly string[]> {
    let params = new HttpParams();
    if (nodeId) params = params.set('nodeId', nodeId);

    return this.http
      .get<SystemLogCategoriesResponse>('/api/v1/system/logs/categories', { params })
      .pipe(map((res) => res.categories));
  }

  createLogStream(filter?: {
    minLevel?: SystemLogLevel;
    category?: string;
    search?: string;
    nodeId?: string | null;
  }): Observable<SystemLogEntry> {
    return new Observable<SystemLogEntry>((subscriber) => {
      const params = new URLSearchParams();
      if (filter?.minLevel) params.set('minLevel', filter.minLevel);
      if (filter?.category) params.set('category', filter.category);
      if (filter?.search) params.set('search', filter.search);
      if (filter?.nodeId) params.set('nodeId', filter.nodeId);

      const queryString = params.toString();
      const url = `/api/v1/system/logs/stream${queryString ? '?' + queryString : ''}`;

      if (typeof EventSource === 'undefined') {
        subscriber.error(new Error('EventSource is not supported in this environment'));
        return;
      }

      const eventSource = new EventSource(url, { withCredentials: true });

      eventSource.onmessage = (event) => {
        try {
          if (!event.data) return;
          const entry = JSON.parse(event.data) as SystemLogEntry;
          subscriber.next(entry);
        } catch {
          // Ignore parse errors on heartbeat or malformed messages
        }
      };

      eventSource.onerror = () => {
        // EventSource will automatically retry connecting.
      };

      return () => {
        eventSource.close();
      };
    });
  }
}
