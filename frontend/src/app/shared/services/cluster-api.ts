import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { NodeResponse, LibraryResponse } from '../models/cluster';

@Injectable({ providedIn: 'root' })
export class ClusterApi {
  private readonly http = inject(HttpClient);
  listNodes() {
    return this.http.get<readonly NodeResponse[]>('/api/v1/nodes');
  }
  listLibraries() {
    return this.http.get<readonly LibraryResponse[]>('/api/v1/libraries');
  }
}
