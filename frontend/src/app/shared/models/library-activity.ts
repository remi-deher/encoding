export type LibraryActivityStatus = 'untreated' | 'active' | 'done' | 'failed' | 'disabled';

export interface LibraryActivityFile {
  readonly id: string;
  readonly libraryId: string;
  readonly library: string;
  readonly relativePath: string;
  readonly bytes: number;
  readonly modifiedAt: string;
  readonly libraryActive: boolean;
  readonly flow: string | null;
  readonly flowVersion: number | null;
  readonly status: LibraryActivityStatus;
  readonly executionId: string | null;
  readonly executionState: string | null;
  readonly detail: string | null;
  readonly nodeId: string | null;
  readonly node: string | null;
  readonly progress: number | null;
  readonly updatedAt: string | null;
}

export interface LibraryActivityCounts {
  readonly untreated: number;
  readonly active: number;
  readonly done: number;
  readonly failed: number;
  readonly disabled: number;
}

export interface LibraryActivityResponse {
  readonly files: readonly LibraryActivityFile[];
  readonly counts: LibraryActivityCounts;
  readonly totalCount: number;
  readonly limit: number;
  readonly offset: number;
  readonly observedAt: string;
}
