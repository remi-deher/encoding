import { HttpClient } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { catchError, of } from 'rxjs';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { DetailField, DetailFieldListComponent } from '../../shared/ui/detail-field-list.component';
import { MEDIA_TYPE_OPTIONS, MediaType } from '../../shared/models/media-type';
import { RecordFormDialogComponent, RecordFormDialogData } from '../../shared/ui/record-form-dialog.component';
import { NotificationService } from '../../shared/services/notification.service';

interface LibraryResponse {
  readonly id: string;
  readonly name: string;
  readonly mediaType: MediaType;
  readonly createdAt: string;
}

interface LibraryRecord extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly category: MediaType;
  readonly mediaType: string;
  readonly configuredNodes: string;
  readonly accessModes: string;
  readonly nodePaths: string;
  readonly lastScan: string;
  readonly associatedFlows: string;
  readonly state: string;
  readonly modifiedAt: string;
  readonly modifiedOrder: number;
}

const mediaLabel = (mediaType: MediaType): string => MEDIA_TYPE_OPTIONS.find((option) => option.value === mediaType)?.label ?? mediaType;

@Component({
  selector: 'app-library-catalog-page',
  imports: [DataTableComponent, DetailFieldListComponent, ManagementCatalogTemplateComponent, CategoryTabsComponent, MatButtonModule],
  templateUrl: './library-catalog-page.component.html',
})
export class LibraryCatalogPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, { initialValue: this.route.snapshot.data });
  private readonly http = inject(HttpClient);
  private readonly dialog = inject(MatDialog);
  private readonly notifications = inject(NotificationService);
  private readonly libraries = signal<readonly LibraryRecord[]>([]);

  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Bibliothèques');
  protected readonly description = computed(() => (this.routeData()['description'] as string) ?? '');
  protected readonly selectedCategory = signal('all');
  protected readonly selectedLibrary = signal<LibraryRecord | null>(null);
  protected readonly detailTitle = computed(() => this.selectedLibrary()?.name ?? 'Bibliothèque');
  protected readonly detailDescription = computed(() => this.selectedLibrary()?.mediaType ?? 'Détails de la bibliothèque');
  protected readonly detailFields = computed<readonly DetailField[]>(() => {
    const library = this.selectedLibrary();
    return library ? [
      { section: 'Bibliothèque', label: 'Type de média', value: library.mediaType },
      { section: 'Bibliothèque', label: 'État', value: library.state },
      { section: 'Accès par nœud', label: 'Nœuds concernés', value: library.configuredNodes },
      { section: 'Accès par nœud', label: 'Mode de transfert', value: library.accessModes },
      { section: 'Accès par nœud', label: 'Points d’entrée', value: library.nodePaths },
      { section: 'Activité', label: 'Dernière analyse', value: library.lastScan },
      { section: 'Activité', label: 'Flows associés', value: library.associatedFlows },
      { section: 'Activité', label: 'Créée le', value: library.modifiedAt },
    ] : [];
  });

  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Nom', sortable: true },
    { key: 'mediaType', label: 'Type', sortable: true },
    { key: 'configuredNodes', label: 'Accès déclarés', sortable: true },
    { key: 'accessModes', label: 'Modes d’accès', sortable: true },
    { key: 'state', label: 'État', sortable: true },
    { key: 'modifiedAt', label: 'Créée le', sortable: true, sortValueKey: 'modifiedOrder' },
  ];

  protected readonly categoryOptions = computed<readonly CategoryTabOption[]>(() => {
    const libraries = this.libraries();
    return [
      { value: 'all', label: 'Tous', count: libraries.length },
      ...MEDIA_TYPE_OPTIONS.map((category) => ({ ...category, count: libraries.filter((library) => library.category === category.value).length })),
    ];
  });

  protected readonly filteredLibraries = computed(() => {
    const category = this.selectedCategory();
    return this.libraries().filter((library) => category === 'all' || library.category === category);
  });

  constructor() {
    this.refresh();
  }

  protected onCategoryChange(category: string): void { this.selectedCategory.set(category); }
  protected onLibraryOpen(row: DataTableRow): void { this.selectedLibrary.set(this.libraries().find((library) => library.id === row['id']) ?? null); }
  protected onDetailClosed(): void { this.selectedLibrary.set(null); }

  protected addLibrary(): void {
    const data: RecordFormDialogData = {
      title: 'Ajouter une bibliothèque',
      description: 'Créez sa définition partagée. Les chemins et modes d’accès seront configurés séparément pour chaque nœud.',
      submitLabel: 'Créer la bibliothèque',
      fields: [
        { id: 'name', label: 'Nom', type: 'text', required: true, maxLength: 128 },
        { id: 'mediaType', label: 'Type de média', type: 'select', required: true, options: MEDIA_TYPE_OPTIONS },
      ],
    };
    this.dialog.open(RecordFormDialogComponent, { data, width: 'min(520px, 100vw)', maxWidth: '100vw', autoFocus: 'first-tabbable', restoreFocus: true })
      .afterClosed().subscribe((value: Record<string, string> | undefined) => {
        if (!value) return;
        this.http.post<LibraryResponse>('/api/v1/libraries', value).pipe(
          catchError((error: { status?: number }) => {
            const message = error.status === 409
              ? 'Une bibliothèque porte déjà ce nom.'
              : 'La bibliothèque n’a pas pu être créée. Vérifiez que le serveur est disponible.';
            this.notifications.show(message);
            return of(null);
          }),
        ).subscribe((created) => {
          if (!created) return;
          this.libraries.update((current) => [...current, this.toRecord(created)]);
          this.notifications.show(`Bibliothèque « ${created.name} » créée.`);
        });
      });
  }

  private refresh(): void {
    this.http.get<readonly LibraryResponse[]>('/api/v1/libraries').pipe(
      catchError(() => {
        this.notifications.show('Impossible de charger les bibliothèques depuis le serveur.');
        return of([]);
      }),
    ).subscribe((libraries) => this.libraries.set(libraries.map((library) => this.toRecord(library))));
  }

  private toRecord(library: LibraryResponse): LibraryRecord {
    const date = new Date(library.createdAt);
    return {
      id: library.id,
      name: library.name,
      category: library.mediaType,
      mediaType: mediaLabel(library.mediaType),
      configuredNodes: 'Aucun accès',
      accessModes: 'Non configuré',
      nodePaths: 'Aucun point d’entrée configuré',
      lastScan: 'Jamais',
      associatedFlows: 'Aucun flow associé',
      state: 'Accès à configurer',
      modifiedAt: new Intl.DateTimeFormat('fr-FR', { dateStyle: 'medium' }).format(date),
      modifiedOrder: date.getTime(),
    };
  }
}
