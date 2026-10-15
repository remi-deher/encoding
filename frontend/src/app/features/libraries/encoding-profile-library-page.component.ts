import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, signal } from '@angular/core';
import { FormControl, FormGroup, FormRecord, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { firstValueFrom } from 'rxjs';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { CategoryTabOption, CategoryTabsComponent } from '../../shared/ui/category-tabs.component';
import { DataTableColumn, DataTableComponent, DataTableRow } from '../../shared/ui/data-table.component';
import { ManagementCatalogTemplateComponent } from '../../shared/templates/management-catalog-template.component';
import { EncodingProfiles } from '../../shared/services/encoding-profiles';
import {
  EncodingProfileDetails,
  EncodingProfileDraft,
  EncodingProfileSummary,
  VideoEncoderDescriptor,
} from '../../shared/models/encoding-profiles';

interface ProfileRow extends DataTableRow {
  readonly id: string;
  readonly name: string;
  readonly version: string;
  readonly encoderCount: string;
  readonly state: string;
  readonly description: string;
}

interface EncoderGroup {
  readonly codec: string;
  readonly label: string;
  readonly encoders: readonly VideoEncoderDescriptor[];
}

@Component({
  selector: 'app-encoding-profile-library-page',
  imports: [
    CategoryTabsComponent,
    DataTableComponent,
    ManagementCatalogTemplateComponent,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    ReactiveFormsModule,
  ],
  template: `
    <app-management-catalog-template
      [title]="title()"
      filterLabel="Profils d’encodage"
      searchLabel="Rechercher un profil"
      [searchQuery]="query()"
      (searchQueryChange)="query.set($event)"
      note="Un profil associe chaque encodeur à sa propre cible de qualité. Le mode automatique choisit le premier encodeur compatible avec les capacités du worker."
      [catalogState]="catalogState()"
      [catalogMessage]="catalogMessage()"
    >
      <ng-template #pageActions>
        <button matButton="filled" type="button" (click)="newProfile()">Nouveau profil</button>
      </ng-template>
      <app-category-tabs
        catalogue-navigation
        label="Type de librairie"
        [options]="librarySections"
        selected="encoding"
        (selectedChange)="onSectionChanged($event)"
      />
      <div catalogue-table class="profile-layout">
        <section class="profile-list" aria-label="Profils d’encodage enregistrés">
          <app-data-table layout="cards"
            [columns]="columns"
            [rows]="filteredRows()"
            [rowOpenable]="true"
            rowOpenLabel="Consulter le profil"
            (rowOpen)="openProfile($event)"
            emptyMessage="Aucun profil d’encodage."
          />
        </section>
        <section class="profile-editor" aria-label="Éditeur de profil d’encodage">
          @if (formOpen()) {
            <form [formGroup]="metadataForm" (ngSubmit)="saveDraft()">
              <header class="editor-heading">
                <div>
                  <h2>{{ metadataForm.controls.name.value || 'Profil sans nom' }}</h2>
                  @if (details(); as current) {
                    <p>Brouillon r{{ current.profile.draftRevision }} · {{ current.profile.publishedVersion }} version(s) publiée(s)</p>
                  } @else {
                    <p>Nouveau brouillon · non publié</p>
                  }
                </div>
                @if (readOnly()) { <span class="profile-badge">Intégré · lecture seule</span> }
              </header>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Nom du profil</mat-label>
                <input matInput formControlName="name" maxlength="128" [readonly]="readOnly() || busy()" />
              </mat-form-field>
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>Description</mat-label>
                <textarea matInput rows="2" formControlName="description" maxlength="1000" [readonly]="readOnly() || busy()"></textarea>
              </mat-form-field>
              <div class="target-heading">
                <div><h3>Cibles par encodeur</h3><p>Les priorités les plus basses sont essayées en premier en mode automatique.</p></div>
              </div>
              @for (group of encoderGroups(); track group.codec) {
                <section class="codec-group" [attr.aria-label]="group.label">
                  <h4>{{ group.label }}</h4>
                  @for (encoder of group.encoders; track encoder.id) {
                    <div class="encoder-row">
                      <div class="encoder-description">
                        <strong>{{ encoder.name }}</strong>
                        <span>{{ encoder.qualityMode }} · {{ encoder.qualityMinimum }}–{{ encoder.qualityMaximum }}</span>
                      </div>
                      <mat-form-field appearance="outline" subscriptSizing="dynamic">
                        <mat-label>{{ encoder.qualityMode }}</mat-label>
                        <input matInput type="number" [formControl]="targetControl('quality', encoder)" [min]="encoder.qualityMinimum" [max]="encoder.qualityMaximum" [readonly]="readOnly() || busy()" />
                      </mat-form-field>
                      @if (encoder.presets.length) {
                        <mat-form-field appearance="outline" subscriptSizing="dynamic">
                          <mat-label>Preset</mat-label>
                          <mat-select [formControl]="targetControl('preset', encoder)" [disabled]="readOnly() || busy()">
                            @for (preset of encoder.presets; track preset.id) { <mat-option [value]="preset.id">{{ preset.name }}</mat-option> }
                          </mat-select>
                        </mat-form-field>
                      } @else { <span class="no-preset">Preset non pris en charge</span> }
                      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="priority-field">
                        <mat-label>Priorité</mat-label>
                        <input matInput type="number" [formControl]="targetControl('priority', encoder)" min="0" max="999" step="1" [readonly]="readOnly() || busy()" />
                      </mat-form-field>
                    </div>
                  }
                </section>
              }
              @if (message()) { <p class="editor-message" role="status">{{ message() }}</p> }
              <div class="editor-actions">
                @if (readOnly()) {
                  <button matButton="filled" type="button" (click)="newProfile()">Créer un profil personnalisé</button>
                } @else {
                  <button matButton="text" type="button" (click)="cancelEdit()">Annuler</button>
                  <button matButton="outlined" type="submit" [disabled]="busy() || metadataForm.invalid || targetsForm.invalid">{{ busy() ? 'Enregistrement…' : 'Enregistrer le brouillon' }}</button>
                  <button matButton="filled" type="button" [disabled]="busy() || metadataForm.invalid || targetsForm.invalid || !editingId()" (click)="publish()">{{ busy() ? 'Publication…' : 'Publier une version' }}</button>
                }
              </div>
            </form>
          } @else {
            <div class="empty-editor"><h2>Sélectionne un profil</h2><p>Ouvre une ligne pour consulter ses réglages, ou crée un profil personnalisé.</p><button matButton="filled" type="button" (click)="newProfile()">Créer un profil</button></div>
          }
        </section>
      </div>
    </app-management-catalog-template>
  `,
  styles: `
    .profile-layout { display:grid; grid-template-columns:minmax(280px,.85fr) minmax(0,1.6fr); gap:20px; padding:var(--space-4) 20px 20px; }
    .profile-list,.profile-editor { min-width:0; }
    .profile-editor { padding-left:20px; border-left:1px solid var(--border-subtle); }
    form { display:grid; gap:var(--space-3); }
    mat-form-field { width:100%; }
    .editor-heading,.target-heading { display:flex; justify-content:space-between; align-items:flex-start; gap:var(--space-3); }
    h2,h3,h4,p { margin:0; }
    h2 { font-size:var(--font-title); font-weight:600; }
    h3 { font-size:var(--font-body); }
    h4 { margin:0 0 10px; font-size: var(--font-body); }
    .editor-heading p,.target-heading p { margin-top:var(--space-1); color:var(--text-muted); font-size: var(--font-body); }
    .profile-badge,.no-preset { color:var(--text-secondary); font-size: var(--font-body); }
    .codec-group { display:grid; gap:var(--space-2); padding:var(--space-3) 0; border-top:1px solid var(--border-subtle); }
    .encoder-row { display:grid; grid-template-columns:minmax(125px,1fr) minmax(90px,.55fr) minmax(150px,.8fr) minmax(82px,.45fr); gap:10px; align-items:center; }
    .encoder-description { display:grid; gap:3px; min-width:0; }
    .encoder-description strong { font-size: var(--font-body); }
    .encoder-description span { color:var(--text-muted); font-size: var(--font-body); }
    .encoder-row mat-form-field { margin:0; }
    .no-preset { padding:0 var(--space-2); }
    .editor-actions { display:flex; flex-wrap:wrap; justify-content:flex-end; gap:var(--space-2); padding-top:6px; }
    .editor-message { color:var(--text-secondary); font-size: var(--font-body); }
    .empty-editor { display:grid; align-content:center; justify-items:start; gap:10px; min-height:260px; }
    @media(max-width:1000px) { .profile-layout { grid-template-columns:1fr; } .profile-editor { padding:18px 0 0; border-left:0; border-top:1px solid var(--border-subtle); } }
    @media(max-width:620px) { .profile-layout { padding:var(--space-3); } .encoder-row { grid-template-columns:1fr 1fr; } .encoder-description { grid-column:1/-1; } .profile-badge { display:none; } }
  `,
})
export class EncodingProfileLibraryPageComponent {
  private readonly api = inject(EncodingProfiles);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly routeData = toSignal(this.route.data, { initialValue: this.route.snapshot.data });
  protected readonly title = computed(() => (this.routeData()['title'] as string) ?? 'Librairies');
  protected readonly librarySections: readonly CategoryTabOption[] = [
    { value: 'media', label: 'Médias' },
    { value: 'scripts', label: 'Scripts' },
    { value: 'templates', label: 'Modèles' },
    { value: 'packs', label: 'Packs de composants' },
    { value: 'encoding', label: 'Profils d’encodage' },
  ];
  protected readonly catalogState = signal<'ready' | 'loading' | 'error'>('loading');
  protected readonly catalogMessage = signal('Chargement des profils…');
  protected readonly profiles = signal<readonly EncodingProfileSummary[]>([]);
  protected readonly encoders = signal<readonly VideoEncoderDescriptor[]>([]);
  protected readonly details = signal<EncodingProfileDetails | null>(null);
  protected readonly editingId = signal<string | null>(null);
  protected readonly readOnly = signal(false);
  protected readonly formOpen = signal(false);
  protected readonly busy = signal(false);
  protected readonly message = signal('');
  protected readonly query = signal('');
  protected readonly metadataForm = new FormGroup({
    name: new FormControl('', { nonNullable: true, validators: [Validators.required, Validators.maxLength(128)] }),
    description: new FormControl('', { nonNullable: true, validators: [Validators.maxLength(1000)] }),
  });
  protected readonly targetsForm = new FormRecord<FormControl<string>>({});
  protected readonly columns: readonly DataTableColumn[] = [
    { key: 'name', label: 'Profil', sortable: true },
    { key: 'version', label: 'Version publiée', sortable: true },
    { key: 'encoderCount', label: 'Encodeurs', sortable: true },
    { key: 'state', label: 'État', sortable: true },
  ];
  protected readonly encoderGroups = computed<readonly EncoderGroup[]>(() => {
    const codecs = [
      { id: 'hevc', label: 'H.265 / HEVC' },
      { id: 'h264', label: 'H.264' },
      { id: 'av1', label: 'AV1' },
    ];
    return codecs.map((codec) => ({
      codec: codec.id,
      label: codec.label,
      encoders: this.encoders().filter((encoder) => encoder.codec === codec.id),
    }));
  });
  protected readonly rows = computed<readonly ProfileRow[]>(() => this.profiles().map((profile) => ({
    id: profile.id,
    name: profile.draftName,
    version: profile.publishedVersion ? `v${profile.publishedVersion}` : '—',
    encoderCount: String(profile.encoderCount),
    state: profile.isBuiltIn ? 'Intégré' : profile.publishedVersion ? 'Publié + brouillon' : 'Brouillon',
    description: profile.draftDescription,
  })));
  protected readonly filteredRows = computed(() => {
    const query = this.query().trim().toLocaleLowerCase();
    return query ? this.rows().filter((row) => [row.name, row.description, row.state].some((value) => value.toLocaleLowerCase().includes(query))) : this.rows();
  });

  constructor() { void this.load(); }

  protected onSectionChanged(section: string): void {
    if (section === 'media') void this.router.navigateByUrl('/libraries');
    if (section === 'scripts') void this.router.navigateByUrl('/libraries/scripts');
    if (section === 'templates') void this.router.navigateByUrl('/libraries/templates');
    if (section === 'packs') void this.router.navigateByUrl('/libraries/packs');
  }

  protected targetControl(kind: 'quality' | 'preset' | 'priority', encoder: VideoEncoderDescriptor): FormControl<string> {
    return this.targetsForm.controls[`${kind}:${encoder.id}`];
  }

  protected async openProfile(row: DataTableRow): Promise<void> {
    const id = String(row['id']);
    const profile = this.profiles().find((candidate) => candidate.id === id);
    if (!profile) return;
    this.busy.set(true);
    this.message.set('');
    try {
      const details = await this.api.details(id);
      if (this.profiles().some((candidate) => candidate.id === id)) this.setDetails(details);
    } catch { this.message.set('Impossible de charger le profil.'); }
    finally { this.busy.set(false); }
  }

  protected newProfile(): void {
    this.details.set(null);
    this.editingId.set(null);
    this.readOnly.set(false);
    this.formOpen.set(true);
    this.message.set('');
    this.metadataForm.reset({ name: '', description: '' });
    this.resetTargets(this.encoders().map((encoder, index) => ({
      encoderId: encoder.id,
      quality: encoder.defaultQuality,
      preset: encoder.presets.find((preset) => preset.id === 'medium')?.id ?? '',
      priority: (encoder.acceleration === 'nvenc' ? 0 : encoder.acceleration === 'qsv' ? 100 : encoder.acceleration === 'vaapi' ? 200 : 300) + index,
    })));
  }

  protected cancelEdit(): void {
    this.details.set(null);
    this.editingId.set(null);
    this.formOpen.set(false);
    this.message.set('');
  }

  protected async saveDraft(): Promise<void> {
    if (this.busy() || this.metadataForm.invalid || this.targetsForm.invalid) return;
    this.busy.set(true);
    try {
      const draft = this.makeDraft();
      const id = this.editingId();
      const saved = id
        ? await this.api.saveDraft(id, this.details()?.profile.draftRevision ?? 1, draft)
        : await this.api.create(draft);
      this.setDetails(saved);
      this.message.set('Brouillon enregistré.');
      await this.reloadList();
    } catch (error) { this.message.set(this.errorMessage(error, 'Enregistrement impossible. Vérifiez les cibles et leurs priorités.')); }
    finally { this.busy.set(false); }
  }

  protected async publish(): Promise<void> {
    if (!this.editingId() || this.busy() || this.metadataForm.invalid || this.targetsForm.invalid) return;
    this.busy.set(true);
    try {
      const revision = this.details()?.profile.draftRevision ?? 1;
      const version = await this.api.publish(this.editingId()!, revision);
      const details = await this.api.details(version.profileId);
      this.setDetails(details);
      this.message.set(`Version ${version.version} publiée.`);
      await this.reloadList();
    } catch (error) { this.message.set(this.errorMessage(error, 'Publication impossible. Enregistrez ou rechargez le brouillon.')); }
    finally { this.busy.set(false); }
  }

  private setDetails(details: EncodingProfileDetails): void {
    this.details.set(details);
    this.editingId.set(details.profile.isBuiltIn ? null : details.profile.id);
    this.readOnly.set(details.profile.isBuiltIn);
    this.formOpen.set(true);
    this.metadataForm.reset({ name: details.draft.name, description: details.draft.description });
    this.resetTargets(details.draft.targets);
  }

  private resetTargets(targets: EncodingProfileDraft['targets']): void {
    for (const key of Object.keys(this.targetsForm.controls)) this.targetsForm.removeControl(key);
    for (const encoder of this.encoders()) {
      const target = targets.find((candidate) => candidate.encoderId === encoder.id);
      this.targetsForm.addControl(`quality:${encoder.id}`, new FormControl(String(target?.quality ?? encoder.defaultQuality), { nonNullable: true, validators: [Validators.required, Validators.min(encoder.qualityMinimum), Validators.max(encoder.qualityMaximum)] }));
      this.targetsForm.addControl(`preset:${encoder.id}`, new FormControl(target?.preset ?? encoder.presets.find((preset) => preset.id === 'medium')?.id ?? '', { nonNullable: true }));
      this.targetsForm.addControl(`priority:${encoder.id}`, new FormControl(String(target?.priority ?? 999), { nonNullable: true, validators: [Validators.required, Validators.min(0), Validators.max(999), Validators.pattern(/^\d+$/)] }));
    }
    if (this.readOnly()) { this.metadataForm.disable({ emitEvent: false }); this.targetsForm.disable({ emitEvent: false }); }
    else { this.metadataForm.enable({ emitEvent: false }); this.targetsForm.enable({ emitEvent: false }); }
  }

  private makeDraft(): EncodingProfileDraft {
    const values = this.metadataForm.getRawValue();
    return {
      name: values.name.trim(),
      description: values.description.trim(),
      targets: this.encoders().map((encoder) => ({
        encoderId: encoder.id,
        quality: Number(this.targetControl('quality', encoder).value),
        preset: this.targetControl('preset', encoder).value,
        priority: Number(this.targetControl('priority', encoder).value),
      })),
    };
  }

  private async load(): Promise<void> {
    try {
      const [profiles, encoders] = await Promise.all([firstValueFrom(this.api.list()), firstValueFrom(this.api.encoders())]);
      this.profiles.set(profiles);
      this.encoders.set(encoders);
      this.catalogState.set('ready');
      this.catalogMessage.set('');
    } catch (error) {
      this.catalogState.set('error');
      this.catalogMessage.set(this.errorMessage(error, 'Les profils d’encodage ne sont pas disponibles.'));
    }
  }

  private async reloadList(): Promise<void> {
    this.profiles.set(await firstValueFrom(this.api.list()));
  }

  private errorMessage(error: unknown, fallback: string): string {
    return error instanceof HttpErrorResponse ? error.error?.errors?.targets?.[0] ?? error.error?.title ?? fallback : fallback;
  }
}
