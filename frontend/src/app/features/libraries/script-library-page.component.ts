import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatListModule } from '@angular/material/list';
import { MatSelectModule } from '@angular/material/select';
import { toSignal } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { CodeEditorComponent } from '../../shared/ui/code-editor.component';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { NotificationService } from '../../shared/services/notification.service';
import { FlowScripts } from '../../shared/services/flow-scripts';
import {
  FlowScriptDetails,
  FlowScriptDraft,
  FlowScriptLanguage,
  FlowScriptSummary,
} from '../../shared/models/flow-scripts';

@Component({
  selector: 'app-script-library-page',
  imports: [
    CategoryTabsComponent,
    CodeEditorComponent,
    ManagementCatalogTemplateComponent,
    MatButtonModule,
    MatFormFieldModule,
    MatListModule,
    MatSelectModule,
    ReactiveFormsModule,
  ],
  template: `
    <app-management-catalog-template
      [title]="title()"
      filterLabel="Scripts"
      searchLabel="Rechercher un script"
      [searchQuery]="query()"
      (searchQueryChange)="query.set($event)"
      note="Les flows publiés conservent le code et la version de script choisis. Modifier un script crée un brouillon; seule sa publication crée une nouvelle version."
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
    >
      <ng-template #pageActions>
        <button matButton="filled" type="button" (click)="newScript()">Nouveau script</button>
      </ng-template>

      <app-category-tabs
        catalogue-navigation
        label="Type de librairie"
        [options]="librarySections"
        selected="scripts"
        (selectedChange)="onSectionChanged($event)"
      />

      <div catalogue-table class="script-workspace">
        <aside class="script-rail" aria-label="Scripts enregistrés">
          <div class="rail-heading">
            <h2>Scripts</h2>
            <span>{{ filteredScripts().length }}</span>
          </div>
          @if (filteredScripts().length) {
            <mat-selection-list
              class="script-list"
              aria-label="Choisir un script à modifier"
              [multiple]="false"
              [disabled]="busy()"
              (selectionChange)="selectScript($event.options[0]?.value)"
            >
              @for (script of filteredScripts(); track script.id) {
                <mat-list-option
                  class="script-option"
                  [value]="script.id"
                  [selected]="script.id === editingId()"
                >
                  <span class="script-option-name">{{ script.draftName }}</span>
                  <span class="script-option-language">{{
                    script.draftLanguage === 'python' ? 'Python' : 'JavaScript'
                  }}</span>
                  <span class="script-option-version">
                    @if (script.publishedVersion > 0) {
                      Publié · v{{ script.publishedVersion }}
                    } @else {
                      Non publié
                    }
                    · brouillon r{{ script.draftRevision }}
                  </span>
                </mat-list-option>
              }
            </mat-selection-list>
          } @else {
            <p class="script-list-empty">
              @if (query().trim()) {
                Aucun script ne correspond à la recherche.
              } @else {
                Aucun script enregistré pour le moment.
              }
            </p>
          }
        </aside>

        <section class="script-editor" aria-label="Éditeur de script">
          @if (formOpen()) {
            <form [formGroup]="form" (ngSubmit)="saveDraft()">
              <header class="editor-heading">
                <div class="editor-title">
                  <h2>
                    {{
                      editingId() ? form.controls.name.value || 'Script sans nom' : 'Nouveau script'
                    }}
                  </h2>
                  @if (details(); as current) {
                    <p class="editor-version">
                      Brouillon r{{ current.script.draftRevision }}
                      @if (current.script.publishedVersion > 0) {
                        · {{ current.script.publishedVersion }} version(s) publiée(s)
                      } @else {
                        · aucune version publiée
                      }
                    </p>
                  } @else {
                    <p class="editor-version">Nouveau brouillon · non publié</p>
                  }
                </div>
                <span class="script-language-badge">{{
                  form.controls.language.value === 'python' ? 'Python' : 'JavaScript'
                }}</span>
              </header>

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
              <mat-form-field
                appearance="outline"
                subscriptSizing="dynamic"
                class="description-field"
              >
                <mat-label>Description</mat-label>
                <input
                  matInput
                  formControlName="description"
                  maxlength="1000"
                  [disabled]="busy()"
                />
              </mat-form-field>

              <div class="editor-toolbar">
                <span
                  >Code source ·
                  {{ form.controls.language.value === 'python' ? 'Python' : 'JavaScript' }}</span
                >
                <span>Durée maximale réglée dans chaque bloc de flow</span>
              </div>
              <app-code-editor
                [value]="form.controls.code.value"
                [language]="form.controls.language.value"
                [disabled]="busy()"
                [roomy]="true"
                [label]="'Code de ' + (form.controls.name.value || 'script')"
                (valueChange)="setCode($event)"
              />
              <details class="script-contract">
                <summary>Contexte remis au script</summary>
                <div class="contract-content">
                  <p>
                    <code>context.variables</code>, <code>context.metadata</code>,
                    <code>context.sourceMetadata</code>, <code>context.currentFile</code>
                  </p>
                  <p>
                    Le script peut retourner <code>outputId</code>, <code>variables</code> et, pour
                    un résultat validé, <code>candidate</code>.
                  </p>
                </div>
              </details>
              @if (details()?.script.versions?.length) {
                <div class="published-versions" aria-label="Versions publiées">
                  <h3>Versions publiées</h3>
                  <span>{{ versionSummary() }}</span>
                </div>
              }
              @if (message()) {
                <p class="save-message" role="status">{{ message() }}</p>
              }
              <footer class="editor-actions">
                <span class="published-version">
                  @if (details()?.script.publishedVersion; as version) {
                    Version publiée v{{ version }}
                  } @else {
                    Aucune version publiée
                  }
                </span>
                <div class="editor-action-buttons">
                  <button matButton="text" type="button" (click)="cancelEdit()">Annuler</button>
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
                </div>
              </footer>
            </form>
          } @else {
            <div class="empty-editor">
              <h2>Choisissez un script</h2>
              <p>
                Sélectionnez un script dans la liste pour modifier son brouillon, ou créez-en un
                nouveau.
              </p>
              <button matButton="filled" type="button" (click)="newScript()">
                Nouveau script
              </button>
            </div>
          }
        </section>
      </div>
    </app-management-catalog-template>
  `,
  styles: [
    `
      .script-workspace {
        display: grid;
        grid-template-columns: minmax(210px, 0.27fr) minmax(0, 1fr);
        min-height: 530px;
      }
      .script-rail {
        min-width: 0;
        padding: 18px 10px;
        border-right: 1px solid var(--border-subtle);
        background: var(--surface-muted);
      }
      .rail-heading {
        display: flex;
        align-items: center;
        justify-content: space-between;
        gap: 8px;
        padding: 0 10px 10px;
      }
      .rail-heading h2 {
        margin: 0;
        color: var(--text-secondary);
        font-size: 14px;
        font-weight: 700;
        letter-spacing: 0.04em;
        text-transform: uppercase;
      }
      .rail-heading > span {
        color: var(--text-muted);
        font-size: 14px;
      }
      .script-list {
        padding: 0;
      }
      .script-option {
        min-height: 78px;
        height: auto !important;
        margin: 3px 0;
        border-radius: 8px;
      }
      .script-option-name,
      .script-option-language,
      .script-option-version {
        display: block;
        overflow-wrap: anywhere;
      }
      .script-option-name {
        color: var(--text-primary);
        font-size: 14px;
        font-weight: 600;
        line-height: 1.4;
      }
      .script-option-language {
        margin-top: 4px;
        color: var(--accent-strong);
        font-size: 14px;
      }
      .script-option-version {
        margin-top: 3px;
        color: var(--text-muted);
        font-size: 14px;
      }
      .script-list-empty {
        margin: 0;
        padding: 10px;
        color: var(--text-muted);
        font-size: 14px;
        line-height: 1.5;
      }
      .script-editor {
        min-width: 0;
        padding: 20px 24px;
      }
      .editor-heading {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 12px;
        margin: 0 0 20px;
        padding-bottom: 14px;
        border-bottom: 1px solid var(--border-subtle);
      }
      .editor-title {
        min-width: 0;
      }
      .editor-title h2 {
        font-size: 18px;
      }
      h2 {
        margin: 0;
        font-size: 18px;
        font-weight: 600;
      }
      h3 {
        margin: 0 0 6px;
        font-size: 14px;
        font-weight: 600;
      }
      .editor-version {
        margin: 4px 0 0;
        color: var(--text-secondary);
        font-size: 14px;
      }
      .script-language-badge {
        flex: 0 0 auto;
        padding: 5px 9px;
        border-radius: 16px;
        background: var(--accent-soft);
        color: var(--accent-strong);
        font-size: 14px;
      }
      .script-fields {
        display: grid;
        grid-template-columns: minmax(0, 1fr) 210px;
        gap: 16px;
      }
      .description-field {
        width: 100%;
        margin-top: 3px;
      }
      .editor-toolbar {
        display: flex;
        justify-content: space-between;
        flex-wrap: wrap;
        gap: 8px 16px;
        align-items: center;
        margin: 4px 0 10px;
        color: var(--text-secondary);
        font-size: 14px;
      }
      .script-contract {
        margin-top: 12px;
        border-bottom: 1px solid var(--border-subtle);
        color: var(--text-secondary);
        font-size: 14px;
      }
      .script-contract summary {
        min-height: 42px;
        display: flex;
        align-items: center;
        color: var(--text-primary);
        font-weight: 600;
        cursor: pointer;
      }
      .contract-content {
        display: grid;
        gap: 5px;
        padding: 0 0 12px;
      }
      .contract-content p {
        margin: 0;
      }
      .published-versions {
        margin-top: 12px;
        color: var(--text-secondary);
        font-size: 14px;
      }
      .published-versions h3 {
        display: inline;
        margin-right: 8px;
        color: var(--text-primary);
      }
      .save-message {
        margin: 10px 0 0;
        color: var(--color-success);
        font-size: 14px;
      }
      .editor-actions {
        display: flex;
        justify-content: space-between;
        align-items: center;
        flex-wrap: wrap;
        gap: 8px;
        margin: 14px -24px -20px;
        padding: 12px 20px;
        border-top: 1px solid var(--border-subtle);
        background: var(--surface);
      }
      .published-version {
        color: var(--text-muted);
        font-size: 14px;
      }
      .editor-action-buttons {
        display: flex;
        justify-content: flex-end;
        flex-wrap: wrap;
        gap: 8px;
      }
      .empty-editor {
        display: grid;
        align-content: center;
        justify-items: start;
        gap: 10px;
        min-height: 490px;
      }
      .empty-editor p {
        margin: 0 0 5px;
        color: var(--text-secondary);
      }
      @media (max-width: 900px) {
        .script-workspace {
          grid-template-columns: minmax(180px, 0.34fr) minmax(0, 1fr);
        }
        .script-editor {
          padding: 18px 16px;
        }
        .script-fields {
          grid-template-columns: minmax(0, 1fr) 170px;
        }
      }
      @media (max-width: 600px) {
        .script-workspace {
          grid-template-columns: 1fr;
        }
        .script-rail {
          padding: 12px 10px;
          border-right: 0;
          border-bottom: 1px solid var(--border-subtle);
        }
        .script-list {
          display: flex;
          gap: 4px;
          overflow-x: auto;
        }
        .script-option {
          min-width: 185px;
          flex: 0 0 185px;
        }
        .script-editor {
          padding: 16px 12px;
        }
        .script-fields {
          grid-template-columns: 1fr;
          gap: 0;
        }
        .editor-toolbar {
          align-items: flex-start;
          flex-direction: column;
        }
        .editor-actions {
          align-items: stretch;
          flex-direction: column;
          margin: 14px -12px -16px;
          padding: 12px;
        }
        .editor-action-buttons {
          justify-content: stretch;
        }
        .editor-action-buttons button {
          flex: 1;
        }
      }
    `,
  ],
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
  protected readonly versionSummary = computed(() =>
    (this.details()?.script.versions ?? []).map((version) => `v${version.version}`).join(' · '),
  );

  constructor() {
    void this.loadList(true);
  }

  protected onSectionChanged(section: string): void {
    if (section === 'media') void this.router.navigateByUrl('/libraries');
    if (section === 'templates') void this.router.navigateByUrl('/libraries/templates');
    if (section === 'packs') void this.router.navigateByUrl('/libraries/packs');
    if (section === 'encoding') void this.router.navigateByUrl('/libraries/encoding-profiles');
  }

  protected setCode(code: string): void {
    this.form.controls.code.setValue(code);
    this.form.controls.code.markAsDirty();
  }

  protected async selectScript(id: string | undefined): Promise<void> {
    if (!id || id === this.editingId()) return;
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
    this.form.reset({ name: '', language: 'javascript', description: '', code: '' });
  }

  protected cancelEdit(): void {
    const current = this.details();
    if (current) this.openDetails(current);
    else {
      this.formOpen.set(false);
      this.editingId.set(null);
    }
    this.message.set('');
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
