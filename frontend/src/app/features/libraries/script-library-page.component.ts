import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { toSignal } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { CodeEditorComponent } from '../../shared/ui/code-editor.component';
import { ConfirmationDialogComponent } from '../../shared/ui/confirmation-dialog.component';
import {
  DataTableColumn,
  DataTableComponent,
  DataTableRow,
} from '../../shared/ui/data-table.component';
import {
  CatalogEmptyState,
  ManagementCatalogTemplateComponent,
} from '../../shared/templates/management-catalog-template.component';
import { NotificationService } from '../../shared/services/notification.service';
import { FlowScripts } from '../../shared/services/flow-scripts';
import {
  FlowScriptDetails,
  FlowScriptDraft,
  FlowScriptLanguage,
  FlowScriptSummary,
  FlowScriptVersion,
} from '../../shared/models/flow-scripts';

const LANGUAGE_LABELS: Readonly<Record<FlowScriptLanguage, string>> = {
  javascript: 'JavaScript',
  python: 'Python',
};

@Component({
  selector: 'app-script-library-page',
  imports: [
    CategoryTabsComponent,
    CodeEditorComponent,
    DataTableComponent,
    ManagementCatalogTemplateComponent,
    MatButtonModule,
    MatExpansionModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    ReactiveFormsModule,
  ],
  template: `
    <app-management-catalog-template
      [title]="title()"
      filterLabel="Scripts"
      searchLabel="Rechercher un script"
      [showToolbar]="summaries().length > 0"
      [emptyState]="summaries().length === 0 && !formOpen() ? emptyState : null"
      (emptyStateAction)="newScript()"
      [searchQuery]="query()"
      (searchQueryChange)="query.set($event)"
      [note]="note()"
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
      [actionOpen]="formOpen()"
      [actionTitle]="workspaceTitle()"
      [actionDescription]="workspaceStatus()"
      (actionClosed)="closeEditor()"
    >
      <ng-template #pageActions>
        @if (summaries().length > 0) {
          <button matButton="filled" type="button" (click)="newScript()">Nouveau script</button>
        }
      </ng-template>

      <app-category-tabs
        catalogue-navigation
        label="Type de librairie"
        [options]="librarySections"
        selected="scripts"
        (selectedChange)="onSectionChanged($event)"
      />

      <app-data-table
        catalogue-table
        layout="cards"
        [columns]="columns"
        [rows]="rows()"
        [rowOpenable]="true"
        rowOpenLabel="Modifier le script"
        emptyMessage="Aucun script ne correspond à la recherche."
        (rowOpen)="selectScript($event['id'] + '')"
      />

      <ng-template #actionContent>
        <form class="script-form" [formGroup]="form" (ngSubmit)="saveDraft()">
          <div class="script-fields">
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Nom du script</mat-label>
              <input matInput formControlName="name" maxlength="128" [disabled]="busy()" />
              @if (form.controls.name.invalid && form.controls.name.touched) {
                <mat-error>Le nom est obligatoire (128 caractères maximum).</mat-error>
              }
            </mat-form-field>
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
              <mat-label>Langage</mat-label>
              <mat-select formControlName="language" [disabled]="busy()">
                <mat-option value="javascript">JavaScript</mat-option>
                <mat-option value="python">Python</mat-option>
              </mat-select>
            </mat-form-field>
          </div>
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Description</mat-label>
            <input matInput formControlName="description" maxlength="1000" [disabled]="busy()" />
          </mat-form-field>

          <app-code-editor
            [value]="form.controls.code.value"
            [language]="form.controls.language.value"
            [disabled]="busy()"
            [roomy]="true"
            [label]="'Code de ' + (form.controls.name.value || 'script')"
            (valueChange)="setCode($event)"
          />

          <mat-expansion-panel class="script-panel">
            <mat-expansion-panel-header>Contexte remis au script</mat-expansion-panel-header>
            <p>
              <code>context.variables</code>, <code>context.metadata</code>,
              <code>context.sourceMetadata</code>, <code>context.currentFile</code>
            </p>
            <p>
              Le script peut retourner <code>outputId</code>, <code>variables</code> et, pour un
              résultat validé, <code>candidate</code>. La durée maximale se règle dans chaque bloc
              de flow.
            </p>
          </mat-expansion-panel>

          @if (versions().length) {
            <section class="script-versions" aria-label="Versions publiées">
              <h3>Versions publiées</h3>
              @for (version of versions(); track version.version) {
                <mat-expansion-panel class="script-panel" (opened)="loadVersion(version.version)">
                  <mat-expansion-panel-header>
                    v{{ version.version }} · {{ version.name }} ·
                    {{ formatDate(version.createdAt) }}
                  </mat-expansion-panel-header>
                  @if (versionCode(version.version); as published) {
                    <app-code-editor
                      [value]="published.code"
                      [language]="published.language"
                      [disabled]="true"
                      [label]="'Code publié v' + version.version"
                    />
                    <button
                      matButton="outlined"
                      type="button"
                      [disabled]="busy()"
                      (click)="restoreVersion(published)"
                    >
                      Restaurer dans le brouillon
                    </button>
                  } @else {
                    <p role="status">Chargement du code…</p>
                  }
                </mat-expansion-panel>
              }
            </section>
          }

          @if (message()) {
            <p class="save-message" role="status">{{ message() }}</p>
          }
          <footer class="script-actions">
            <button matButton="text" type="button" (click)="requestClose()">Fermer</button>
            <button matButton="outlined" type="submit" [disabled]="busy() || form.invalid">
              {{ busy() ? 'Enregistrement…' : 'Enregistrer le brouillon' }}
            </button>
            <button
              matButton="filled"
              type="button"
              [disabled]="busy() || form.invalid || !form.controls.code.value.trim()"
              (click)="publish()"
            >
              {{ busy() ? 'Publication…' : 'Publier une version' }}
            </button>
          </footer>
        </form>
      </ng-template>
    </app-management-catalog-template>
  `,
  styles: `
    .script-form {
      display: grid;
      gap: var(--space-4);
    }
    .script-fields {
      display: grid;
      grid-template-columns: minmax(0, 1fr) 200px;
      gap: var(--space-3);
    }
    .script-panel p {
      margin: 0 0 var(--space-2);
      color: var(--text-secondary);
    }
    .script-versions {
      display: grid;
      gap: var(--space-2);
    }
    .script-versions h3 {
      margin: 0;
      font-size: var(--font-title);
    }
    .save-message {
      margin: 0;
      color: var(--success);
    }
    .script-actions {
      position: sticky;
      bottom: 0;
      padding: var(--space-3) 0;
      display: flex;
      justify-content: flex-end;
      flex-wrap: wrap;
      gap: var(--space-2);
      border-top: 1px solid var(--border-subtle);
      background: var(--surface);
    }
    @media (max-width: 600px) {
      .script-fields {
        grid-template-columns: 1fr;
      }
      .script-actions button {
        flex: 1 1 auto;
      }
    }
  `,
})
export class ScriptLibraryPageComponent {
  private readonly api = inject(FlowScripts);
  private readonly router = inject(Router);
  private readonly notifications = inject(NotificationService);
  private readonly route = inject(ActivatedRoute);
  protected readonly routeData = toSignal(this.route.data, {
    initialValue: this.route.snapshot.data,
  });
  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Librairies');
  protected readonly librarySections: readonly CategoryTabOption[] = [
    { value: 'media', label: 'Médias' },
    { value: 'scripts', label: 'Scripts' },
    { value: 'templates', label: 'Modèles' },
    { value: 'packs', label: 'Packs de composants' },
    { value: 'encoding', label: 'Profils d’encodage' },
  ];
  protected readonly emptyState: CatalogEmptyState = {
    icon: '</>',
    eyebrow: 'BIBLIOTHÈQUE DE SCRIPTS',
    title: 'Créez votre premier script',
    description:
      'Préparez un script réutilisable dans vos flows. Enregistrez-le en brouillon, puis publiez une version lorsque vous êtes prêt.',
    chips: ['JavaScript', 'Python'],
    actionLabel: 'Créer le premier script',
    footnote: 'Les flows existants conservent le code choisi au moment de leur publication.',
  };
  protected readonly catalogState = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly catalogMessage = signal('Chargement des scripts…');
  protected readonly summaries = signal<readonly FlowScriptSummary[]>([]);
  protected readonly details = signal<FlowScriptDetails | null>(null);
  protected readonly editingId = signal<string | null>(null);
  protected readonly formOpen = signal(false);
  protected readonly busy = signal(false);
  protected readonly message = signal('');
  protected readonly query = signal('');
  protected readonly form = new FormGroup({
    name: new FormControl('', {
      nonNullable: true,
      validators: [Validators.required, Validators.maxLength(128)],
    }),
    language: new FormControl<FlowScriptLanguage>('javascript', { nonNullable: true }),
    description: new FormControl('', {
      nonNullable: true,
      validators: [Validators.maxLength(1000)],
    }),
    code: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(200000)] }),
  });
  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Script', sortable: true },
    { key: 'languageLabel', label: 'Langage', sortable: true },
    { key: 'publicationLabel', label: 'Publication', sortable: true },
    { key: 'revisionLabel', label: 'Brouillon', sortable: true },
    { key: 'updatedLabel', label: 'Modifié le', sortable: true, sortValueKey: 'updatedAt' },
    { key: 'description', label: 'Description' },
  ];
  protected readonly note = computed(() =>
    this.summaries().length
      ? 'Les flows publiés conservent le code et la version de script choisis. Modifier un script crée un brouillon ; seule sa publication crée une nouvelle version.'
      : '',
  );
  protected readonly rows = computed<readonly DataTableRow[]>(() =>
    this.filteredScripts().map((script) => ({
      id: script.id,
      name: script.draftName,
      languageLabel: LANGUAGE_LABELS[script.draftLanguage],
      publicationLabel:
        script.publishedVersion > 0 ? `Publié · v${script.publishedVersion}` : 'Non publié',
      revisionLabel: `r${script.draftRevision}`,
      updatedAt: script.updatedAt,
      updatedLabel: this.formatDate(script.updatedAt),
      description: script.draftDescription || '—',
    })),
  );
  protected readonly versions = computed(() =>
    [...(this.details()?.script.versions ?? [])].sort((a, b) => b.version - a.version),
  );
  private readonly versionCodes = signal<Readonly<Record<number, FlowScriptVersion>>>({});
  private readonly dialog = inject(MatDialog);
  protected readonly filteredScripts = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return query
      ? this.summaries().filter(
          (script) =>
            script.draftName.toLocaleLowerCase().includes(query) ||
            script.draftDescription.toLocaleLowerCase().includes(query),
        )
      : this.summaries();
  });
  protected workspaceTitle(): string {
    if (!this.formOpen()) return 'Scripts';
    return (
      this.form.controls.name.value || (this.editingId() ? 'Script sans nom' : 'Nouveau script')
    );
  }
  protected readonly workspaceStatus = computed(() => {
    const current = this.details();
    if (!this.formOpen()) return `${this.summaries().length} scripts dans la bibliothèque`;
    if (!current) return 'Nouveau brouillon · non publié';
    const publishedStatus =
      current.script.publishedVersion > 0
        ? `${current.script.publishedVersion} version(s) publiée(s)`
        : 'aucune version publiée';
    return `Brouillon r${current.script.draftRevision} · ${publishedStatus}`;
  });

  constructor() {
    void this.loadList(true);
  }

  protected onSectionChanged(section: string): void {
    if (section === 'media') void this.router.navigateByUrl('/libraries');
    if (section === 'templates') void this.router.navigateByUrl('/libraries/templates');
    if (section === 'packs') void this.router.navigateByUrl('/libraries/packs');
    if (section === 'encoding') void this.router.navigateByUrl('/libraries/encoding-profiles');
  }

  protected formatDate(value: string): string {
    return new Date(value).toLocaleDateString('fr-FR');
  }

  protected versionCode(version: number): FlowScriptVersion | undefined {
    return this.versionCodes()[version];
  }

  protected async loadVersion(version: number): Promise<void> {
    const id = this.editingId();
    if (!id || this.versionCodes()[version]) return;
    try {
      const loaded = await this.api.version(id, version);
      this.versionCodes.update((codes) => ({ ...codes, [version]: loaded }));
    } catch {
      this.notifications.show('Impossible de charger cette version.');
    }
  }

  protected restoreVersion(version: FlowScriptVersion): void {
    this.form.patchValue({ language: version.language, code: version.code });
    this.form.markAsDirty();
    this.message.set(
      `Code de la v${version.version} placé dans le brouillon ; enregistrez pour le conserver.`,
    );
  }

  /** Ferme l'éditeur ; demande confirmation si des modifications ne sont pas enregistrées. */
  protected requestClose(): void {
    if (this.busy()) return;
    if (!this.form.dirty) {
      this.closeEditor();
      return;
    }
    this.dialog
      .open(ConfirmationDialogComponent, {
        data: {
          title: 'Abandonner les modifications ?',
          message: 'Le brouillon de ce script contient des modifications non enregistrées.',
          confirmLabel: 'Abandonner',
          confirmColor: 'var(--danger-strong)',
        },
      })
      .afterClosed()
      .subscribe((confirmed: boolean | undefined) => {
        if (confirmed) this.closeEditor();
      });
  }

  protected closeEditor(): void {
    this.formOpen.set(false);
    this.editingId.set(null);
    this.details.set(null);
    this.message.set('');
    this.versionCodes.set({});
    this.form.markAsPristine();
  }

  protected setCode(code: string): void {
    this.form.controls.code.setValue(code);
    this.form.controls.code.markAsDirty();
  }

  protected async selectScript(id: string | undefined): Promise<void> {
    if (!id || id === this.editingId()) return;
    this.versionCodes.set({});
    try {
      const details = await this.api.get(id);
      this.openDetails(details);
    } catch {
      this.notifications.show('Impossible de charger ce script.');
    }
  }

  protected newScript(): void {
    this.details.set(null);
    this.editingId.set(null);
    this.formOpen.set(true);
    this.message.set('');
    this.versionCodes.set({});
    this.form.reset({ name: '', language: 'javascript', description: '', code: '' });
  }

  protected async saveDraft(): Promise<void> {
    const saved = await this.persistDraft();
    if (saved) {
      this.message.set('Brouillon enregistré.');
      this.notifications.show('Brouillon de script enregistré.');
    }
  }

  protected async publish(): Promise<void> {
    const saved = await this.persistDraft();
    if (!saved) return;
    this.busy.set(true);
    try {
      await this.api.publish(saved.script.id, saved.script.draftRevision);
      const refreshed = await this.api.get(saved.script.id);
      this.openDetails(refreshed);
      await this.loadList();
      this.message.set(
        `Version v${refreshed.script.publishedVersion} publiée. Les flows existants conservent leur code.`,
      );
      this.notifications.show(`Version v${refreshed.script.publishedVersion} publiée.`);
    } catch (error) {
      this.showError(error, 'Publication impossible.');
    } finally {
      this.busy.set(false);
    }
  }

  private async persistDraft(): Promise<FlowScriptDetails | null> {
    if (this.busy() || this.form.invalid) {
      this.form.markAllAsTouched();
      return null;
    }
    this.busy.set(true);
    this.message.set('');
    const draft: FlowScriptDraft = this.form.getRawValue();
    try {
      const id = this.editingId();
      const result = id
        ? await this.api.saveDraft(id, this.details()?.script.draftRevision ?? 1, draft)
        : await this.api.create(draft);
      this.openDetails(result);
      await this.loadList();
      return result;
    } catch (error) {
      this.showError(error, 'Enregistrement impossible.');
      return null;
    } finally {
      this.busy.set(false);
    }
  }

  private async loadList(initial = false): Promise<void> {
    if (initial) {
      this.catalogState.set('loading');
      this.catalogMessage.set('Chargement des scripts…');
    }
    try {
      this.summaries.set(await firstValueFrom(this.api.list()));
      this.catalogState.set('ready');
      this.catalogMessage.set('');
    } catch {
      this.catalogState.set('error');
      this.catalogMessage.set('Les scripts ne sont pas disponibles. Réessayez plus tard.');
    }
  }

  private openDetails(details: FlowScriptDetails): void {
    this.details.set(details);
    this.editingId.set(details.script.id);
    this.formOpen.set(true);
    this.message.set('');
    this.form.reset({ ...details.draft });
  }

  private showError(error: unknown, fallback: string): void {
    const title = error instanceof HttpErrorResponse ? error.error?.title : undefined;
    this.message.set(title ?? fallback);
    this.notifications.show(title ?? fallback);
  }
}
