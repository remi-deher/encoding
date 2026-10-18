import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ClusterTool, StorePackage, StoreRepository } from '../models/cluster-extensions';

@Injectable({ providedIn: 'root' })
export class ClusterExtensionsService {
  private readonly http = inject(HttpClient);

  listTools(): Promise<readonly ClusterTool[]> {
    return firstValueFrom(this.http.get<readonly ClusterTool[]>('/api/v1/tools'));
  }

  testToolConformance(toolId: string): Promise<{ toolId: string; passed: boolean; message: string }> {
    return firstValueFrom(
      this.http.post<{ toolId: string; passed: boolean; message: string }>(
        `/api/v1/tools/${toolId}/conformance-test`,
        {},
      ),
    );
  }

  listRepositories(): Promise<readonly StoreRepository[]> {
    return firstValueFrom(this.http.get<readonly StoreRepository[]>('/api/v1/store/repositories'));
  }

  listPackages(): Promise<readonly StorePackage[]> {
    return firstValueFrom(this.http.get<readonly StorePackage[]>('/api/v1/store/packages'));
  }
}
