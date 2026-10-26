import { HttpClient, HttpParams } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { LibraryActivityResponse, LibraryActivityStatus } from '../models/library-activity';

@Injectable({ providedIn: 'root' })
export class LibraryActivity {
  private readonly http = inject(HttpClient);

  list(options: {
    readonly status: LibraryActivityStatus;
    readonly libraryId?: string;
    readonly flow?: string;
    readonly nodeId?: string;
    readonly search?: string;
    readonly limit: number;
    readonly offset: number;
  }) {
    let params = new HttpParams()
      .set('status', options.status)
      .set('limit', String(options.limit))
      .set('offset', String(options.offset));
    if (options.libraryId) params = params.set('libraryId', options.libraryId);
    if (options.flow) params = params.set('flow', options.flow);
    if (options.nodeId) params = params.set('nodeId', options.nodeId);
    if (options.search?.trim()) params = params.set('search', options.search.trim());
    return this.http.get<LibraryActivityResponse>('/api/v1/activity/files', { params });
  }
}
