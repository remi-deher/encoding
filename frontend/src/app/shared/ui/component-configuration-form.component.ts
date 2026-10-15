import { flowFieldValidator } from './flow-configuration-controls';
import { flowConfigurationValue } from '../models/flow-configuration';
import { Component, OnChanges, SimpleChanges, inject, input, output, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ConditionTreeEditorComponent } from './condition-tree-editor.component';
import { ConditionGroup } from '../models/condition-tree';
import { FlowScripts } from '../services/flow-scripts';
import { FlowScriptSummary } from '../models/flow-scripts';
import { EncodingProfiles } from '../services/encoding-profiles';
import {
  EncodingProfileSnapshot,
  EncodingProfileSummary,
  VideoEncoderDescriptor,
} from '../models/encoding-profiles';
import { CodeEditorComponent } from './code-editor.component';
import {
  FlowComponentConfiguration,
  FlowComponentConfigurationField,
  FlowComponentConfigurationSchema,
  FlowComponentConfigurationValue,
} from '../models/flow-component';

type ConfigurationFormGroup = FormGroup<
  Record<string, FormControl<FlowComponentConfigurationValue>>
>;

@Component({
  selector: 'app-component-configuration-form',
  imports: [
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    ReactiveFormsModule,
    ConditionTreeEditorComponent,
    CodeEditorComponent,
  ],
  template: `
    <form class="configuration-form" [formGroup]="form" (ngSubmit)="save()">
      @if (instanceName() !== undefined) {
        <mat-form-field appearance="outline" subscriptSizing="dynamic">
          <mat-label>Nom du bloc</mat-label>
          <input matInput type="text" [formControl]="nameControl" maxlength="128" />
          <mat-hint>Facultatif : un nom adapté au rôle de ce bloc dans le flow.</mat-hint>
        </mat-form-field>
      }
      @if (fields.length) {
        <div class="configuration-fields">
          @for (field of fields; track field.id) {
            @if (isFieldVisible(field)) {
              @switch (field.type) {
                @case ('condition-tree') {
                  <section
                    class="condition-field"
                    [attr.aria-label]="field.label"
                    [attr.inert]="busy() ? '' : null"
                  >
                    <h3>{{ field.label }}</h3>
                    @if (field.description) {
                      <p>{{ field.description }}</p>
                    }
                    <app-condition-tree-editor
                      [value]="conditionValue(field.id)"
                      [fields]="field.conditionFields"
                      [presets]="field.conditionPresets ?? []"
                      (valueChange)="setConditionValue(field.id, $event)"
                    />
                  </section>
                }
                @case ('boolean') {
                  <div class="boolean-field">
                    <mat-checkbox [formControlName]="field.id">{{ field.label }}</mat-checkbox>
                    @if (field.description) {
                      <p class="field-description">{{ field.description }}</p>
                    }
                  </div>
                }
                @case ('select') {
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>{{ field.label }}</mat-label>
                    <mat-select
                      [formControlName]="field.id"
                      (selectionChange)="onSelectFieldChanged(field.id)"
                    >
                      @for (option of field.options; track option.value) {
                        <mat-option [value]="option.value">{{ option.label }}</mat-option>
                      }
                    </mat-select>
                    @if (field.description) {
                      <mat-hint>{{ field.description }}</mat-hint>
                    }
                    @if (form.controls[field.id].invalid && form.controls[field.id].touched) {
                      <mat-error>{{ form.controls[field.id].errors?.['configuration'] }}</mat-error>
                    }
                  </mat-form-field>
                }
                @case ('script-reference') {
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>{{ field.label }}</mat-label>
                    <mat-select
                      [formControlName]="field.id"
                      [disabled]="busy() || scriptSelectionBusy()"
                      (selectionChange)="selectScriptVersion(field.id, $event.value)"
                    >
                      <mat-option value="">Code intégré au flow</mat-option>
                      @for (script of scripts(); track script.id) {
                        @for (version of script.versions; track version.version) {
                          <mat-option [value]="script.id + '@' + version.version">
                            {{ version.name }} · v{{ version.version }} ·
                            {{ version.language === 'python' ? 'Python' : 'JavaScript' }}
                          </mat-option>
                        }
                      }
                      @if (scripts().length === 0) {
                        <mat-option disabled
                          >Aucune version publiée. Créez-en une dans Librairies /
                          Scripts.</mat-option
                        >
                      }
                    </mat-select>
                    @if (scriptLoadMessage()) {
                      <mat-hint>{{ scriptLoadMessage() }}</mat-hint>
                    } @else if (field.description) {
                      <mat-hint>{{ field.description }}</mat-hint>
                    }
                  </mat-form-field>
                }
                @case ('encoding-profile') {
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>{{ field.label }}</mat-label>
                    <mat-select
                      [value]="profileReference(field.id)"
                      [disabled]="busy() || encodingProfileBusy()"
                      (selectionChange)="selectEncodingProfile(field.id, $event.value)"
                    >
                      @for (profile of encodingProfiles(); track profile.id) {
                        @for (version of profile.versions; track version.version) {
                          <mat-option [value]="profile.id + '@' + version.version">
                            {{ version.name }} · v{{ version.version }}
                            @if (profile.isBuiltIn) {
                              · Intégré
                            }
                          </mat-option>
                        }
                      }
                    </mat-select>
                    @if (encodingProfileMessage()) {
                      <mat-hint>{{ encodingProfileMessage() }}</mat-hint>
                    } @else if (field.description) {
                      <mat-hint>{{ field.description }}</mat-hint>
                    }
                  </mat-form-field>
                  @if (field.id === 'encodingProfile' && profilePreviewEncoders().length) {
                    <section
                      class="profile-target-preview"
                      aria-label="Réglages du profil pour cet encodeur"
                    >
                      <h3>{{ profilePreviewTitle() }}</h3>
                      @if (textValue('encoderMode') !== 'specific') {
                        <p>
                          Le worker utilisera le premier encodeur vérifié disponible selon la
                          priorité du profil.
                        </p>
                      }
                      @for (encoder of profilePreviewEncoders(); track encoder.id) {
                        @if (profileTarget(encoder.id); as target) {
                          <div class="profile-target-row">
                            <strong>{{ encoder.name }}</strong>
                            <span
                              >{{ encoder.qualityMode }} ({{ encoder.qualityOption }}) :
                              {{ target.quality }}</span
                            >
                            @if (encoder.presetOption && target.preset) {
                              <span
                                >{{ encoder.presetOption }} :
                                {{ profilePreset(encoder, target.preset) }}</span
                              >
                            }
                            <span>Priorité {{ target.priority + 1 }}</span>
                          </div>
                        }
                      }
                    </section>
                  }
                }
                @case ('video-encoder') {
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>{{ field.label }}</mat-label>
                    <mat-select [formControlName]="field.id">
                      @for (encoder of availableProfileEncoders(); track encoder.id) {
                        <mat-option [value]="encoder.id">{{ encoder.name }}</mat-option>
                      }
                    </mat-select>
                    <mat-hint
                      >Seuls les encodeurs réglés dans le profil pour ce codec sont
                      proposés.</mat-hint
                    >
                  </mat-form-field>
                }
                @case ('number') {
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>{{ field.label }}</mat-label>
                    <input
                      matInput
                      type="number"
                      [formControlName]="field.id"
                      [attr.min]="field.min ?? null"
                      [attr.max]="field.max ?? null"
                      [attr.step]="field.step ?? null"
                    />
                    @if (field.description) {
                      <mat-hint>{{ field.description }}</mat-hint>
                    }
                    @if (form.controls[field.id].invalid && form.controls[field.id].touched) {
                      <mat-error>{{ form.controls[field.id].errors?.['configuration'] }}</mat-error>
                    }
                  </mat-form-field>
                }
                @case ('text') {
                  @if (field.id === 'code') {
                    <section class="code-field" [attr.aria-label]="field.label">
                      <h3>{{ field.label }}</h3>
                      @if (field.description) {
                        <p class="field-description">{{ field.description }}</p>
                      }
                      <app-code-editor
                        [value]="textValue(field.id)"
                        [language]="scriptLanguage()"
                        [disabled]="busy()"
                        [label]="field.label"
                        (valueChange)="setTextValue(field.id, $event)"
                      />
                      @if (form.controls[field.id].invalid && form.controls[field.id].touched) {
                        <p class="field-error">
                          {{ form.controls[field.id].errors?.['configuration'] }}
                        </p>
                      }
                    </section>
                  } @else {
                    <mat-form-field appearance="outline" subscriptSizing="dynamic">
                      <mat-label>{{ field.label }}</mat-label>
                      @if (field.multiline) {
                        <textarea
                          matInput
                          rows="6"
                          [formControlName]="field.id"
                          [placeholder]="field.placeholder ?? ''"
                        ></textarea>
                      } @else {
                        <input
                          matInput
                          type="text"
                          [formControlName]="field.id"
                          [placeholder]="field.placeholder ?? ''"
                        />
                      }
                      @if (field.description) {
                        <mat-hint>{{ field.description }}</mat-hint>
                      }
                      @if (form.controls[field.id].invalid && form.controls[field.id].touched) {
                        <mat-error>{{
                          form.controls[field.id].errors?.['configuration']
                        }}</mat-error>
                      }
                    </mat-form-field>
                  }
                }
                @case ('secret') {
                  <mat-form-field appearance="outline" subscriptSizing="dynamic">
                    <mat-label>{{ field.label }}</mat-label>
                    <input
                      matInput
                      type="password"
                      autocomplete="new-password"
                      [formControlName]="field.id"
                      [placeholder]="field.placeholder ?? ''"
                    />
                    @if (configuredSecrets().includes(field.id)) {
                      <mat-hint>Laissez vide pour conserver le secret enregistré.</mat-hint>
                    } @else if (field.description) {
                      <mat-hint>{{ field.description }}</mat-hint>
                    }
                    @if (form.controls[field.id].invalid && form.controls[field.id].touched) {
                      <mat-error>{{ form.controls[field.id].errors?.['configuration'] }}</mat-error>
                    }
                  </mat-form-field>
                  @if (configuredSecrets().includes(field.id)) {
                    <mat-checkbox
                      [checked]="secretsToClear.has(field.id)"
                      [disabled]="field.required && !form.controls[field.id].value"
                      (change)="setSecretCleared(field.id, $event.checked)"
                    >
                      Supprimer le secret enregistré
                    </mat-checkbox>
                  }
                }
              }
            }
          }
        </div>
      } @else {
        <p class="empty-configuration">Ce composant ne demande aucun réglage.</p>
      }

      <div class="form-actions">
        <button matButton="text" type="button" (click)="cancelled.emit()">Annuler</button>
        @if (showConnectionTest()) {
          <button
            matButton="outlined"
            type="button"
            [disabled]="busy() || form.invalid || connectionTestBusy()"
            (click)="testConnection()"
          >
            {{ connectionTestBusy() ? 'Test en cours…' : 'Tester la connexion' }}
          </button>
        }
        <button
          matButton="filled"
          type="submit"
          [disabled]="busy() || scriptSelectionBusy() || form.invalid || nameControl.invalid"
        >
          {{ submitLabel() }}
        </button>
      </div>
      @if (connectionTestMessage()) {
        <p
          class="connection-test-result"
          [class.connection-test-failed]="!connectionTestSuccess()"
          aria-live="polite"
        >
          {{ connectionTestMessage() }}
        </p>
      }
    </form>
  `,
  styles: `
    :host {
      display: block;
    }
    .configuration-fields {
      display: grid;
      gap: 8px;
    }
    mat-form-field {
      width: 100%;
    }
    .boolean-field {
      padding: 8px 0;
    }
    .field-description {
      margin: 4px 0 0 32px;
      color: var(--text-muted);
      font-size: 14px;
    }
    .profile-target-preview {
      display: grid;
      gap: 8px;
      padding: 12px;
      border: 1px solid var(--border-subtle);
      border-radius: 9px;
      background: var(--surface-muted);
    }
    .profile-target-preview h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: 14px;
    }
    .profile-target-preview p {
      margin: 0;
      color: var(--text-secondary);
      font-size: 14px;
      line-height: 1.45;
    }
    .profile-target-row {
      display: grid;
      gap: 3px;
      padding: 8px 10px;
      border-radius: 7px;
      background: var(--surface);
      font-size: 14px;
    }
    .profile-target-row strong {
      color: var(--text-primary);
    }
    .profile-target-row span {
      color: var(--text-secondary);
    }
    .empty-configuration {
      margin: 0;
      color: var(--text-secondary);
    }
    .form-actions {
      display: flex;
      justify-content: flex-end;
      gap: 8px;
      padding-top: 16px;
    }
    .connection-test-result {
      margin: 8px 0 0;
      color: var(--color-success, #237a45);
    }
    .connection-test-failed {
      color: var(--color-error, #b3261e);
    }
    @media (max-width: 600px) {
      .form-actions {
        position: sticky;
        bottom: 0;
        margin-inline: -16px;
        padding: 12px 16px;
        background: var(--surface);
        border-top: 1px solid var(--border-subtle);
      }
      .form-actions button {
        flex: 1;
      }
    }
  `,
})
export class ComponentConfigurationFormComponent implements OnChanges {
  private readonly scriptLibrary = inject(FlowScripts);
  private readonly profileLibrary = inject(EncodingProfiles);
  readonly schema = input.required<FlowComponentConfigurationSchema | undefined>();
  readonly values = input<FlowComponentConfiguration>({});
  readonly configuredSecrets = input<readonly string[]>([]);
  readonly instanceName = input<string | undefined>();
  readonly instanceNameChange = output<string>();
  readonly submitLabel = input('Enregistrer');
  readonly busy = input(false);
  readonly showConnectionTest = input(false);
  readonly connectionTestBusy = input(false);
  readonly connectionTestMessage = input('');
  readonly connectionTestSuccess = input(false);
  readonly saved = output<FlowComponentConfiguration>();
  readonly cancelled = output<void>();
  readonly savedWithSecrets = output<{
    readonly values: FlowComponentConfiguration;
    readonly clearSecrets: readonly string[];
  }>();
  readonly connectionTestRequested = output<FlowComponentConfiguration>();

  protected fields: readonly FlowComponentConfigurationField[] = [];
  protected readonly scripts = signal<readonly FlowScriptSummary[]>([]);
  protected readonly encodingProfiles = signal<readonly EncodingProfileSummary[]>([]);
  protected readonly videoEncoders = signal<readonly VideoEncoderDescriptor[]>([]);
  protected readonly scriptLoadMessage = signal('');
  protected readonly scriptSelectionBusy = signal(false);
  protected readonly encodingProfileMessage = signal('');
  protected readonly encodingProfileBusy = signal(false);
  protected secretsToClear = new Set<string>();
  protected form: ConfigurationFormGroup = new FormGroup({});
  protected nameControl = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(128)],
  });

  ngOnChanges(changes: SimpleChanges): void {
    if (
      changes['schema'] ||
      changes['values'] ||
      changes['instanceName'] ||
      changes['configuredSecrets']
    ) {
      this.secretsToClear = new Set();
      this.nameControl.setValue(this.instanceName() ?? '');
      this.fields = this.schema()?.fields ?? [];
      const controls: Record<string, FormControl<FlowComponentConfigurationValue>> = {};
      for (const field of this.fields) {
        controls[field.id] = new FormControl(flowConfigurationValue(field, this.values()), {
          validators: flowFieldValidator(field),
        });
      }
      this.form = new FormGroup(controls);
      if (this.fields.some((field) => field.type === 'script-reference')) this.loadScripts();
      if (this.fields.some((field) => field.type === 'encoding-profile'))
        this.loadEncodingProfiles();
    }
    if (this.busy()) {
      this.form.disable({ emitEvent: false });
      this.nameControl.disable({ emitEvent: false });
    } else {
      this.form.enable({ emitEvent: false });
      this.nameControl.enable({ emitEvent: false });
    }
  }

  protected save(): void {
    if (this.busy() || this.form.invalid || this.nameControl.invalid) return;
    if (this.instanceName() !== undefined)
      this.instanceNameChange.emit(this.nameControl.value.trim());
    const values = this.form.getRawValue();
    this.saved.emit(values);
    this.savedWithSecrets.emit({ values, clearSecrets: [...this.secretsToClear] });
  }

  protected testConnection(): void {
    if (this.busy() || this.form.invalid || this.connectionTestBusy()) return;
    this.connectionTestRequested.emit(this.form.getRawValue());
  }

  protected conditionValue(fieldId: string): ConditionGroup | undefined {
    const value = this.form.controls[fieldId]?.value;
    return value && typeof value === 'object' && 'kind' in value ? value : undefined;
  }
  protected setConditionValue(fieldId: string, value: ConditionGroup): void {
    if (this.busy()) return;
    const control = this.form.controls[fieldId];
    control.setValue(value);
    control.markAsDirty();
    control.markAsTouched();
  }

  protected setSecretCleared(fieldId: string, cleared: boolean): void {
    if (this.busy()) return;
    const updated = new Set(this.secretsToClear);
    if (cleared) updated.add(fieldId);
    else updated.delete(fieldId);
    this.secretsToClear = updated;
  }

  protected scriptLanguage(): 'javascript' | 'python' {
    return this.form.controls['language']?.value === 'python' ? 'python' : 'javascript';
  }

  protected isFieldVisible(field: FlowComponentConfigurationField): boolean {
    if (!field.visibleWhenFieldId) return true;
    const value = this.form.controls[field.visibleWhenFieldId]?.value;
    if (field.visibleWhenEmpty) return value === undefined || value === null || value === '';
    return field.visibleWhenValue === undefined || value === field.visibleWhenValue;
  }

  protected profileReference(fieldId: string): string {
    const value = this.form.controls[fieldId]?.value;
    return isEncodingProfileSnapshot(value) ? `${value.id}@${value.version}` : '';
  }

  protected availableProfileEncoders(): readonly VideoEncoderDescriptor[] {
    const profile = this.form.controls['encodingProfile']?.value;
    const codec = this.form.controls['videoCodec']?.value;
    if (!isEncodingProfileSnapshot(profile) || typeof codec !== 'string') return [];
    const configured = new Set(profile.targets.map((target) => target.encoderId));
    return this.videoEncoders().filter(
      (encoder) => encoder.codec === codec && configured.has(encoder.id),
    );
  }

  protected profilePreviewEncoders(): readonly VideoEncoderDescriptor[] {
    const encoders = this.availableProfileEncoders();
    if (this.textValue('encoderMode') === 'specific') {
      const selected = this.form.controls['encoderId']?.value;
      return encoders.filter((encoder) => encoder.id === selected);
    }
    return [...encoders].sort(
      (left, right) =>
        (this.profileTarget(left.id)?.priority ?? Number.MAX_SAFE_INTEGER) -
        (this.profileTarget(right.id)?.priority ?? Number.MAX_SAFE_INTEGER),
    );
  }

  protected profilePreviewTitle(): string {
    return this.textValue('encoderMode') === 'specific'
      ? 'Réglages de l’encodeur sélectionné'
      : 'Cibles du profil pour ce codec';
  }

  protected profileTarget(encoderId: string) {
    const profile = this.form.controls['encodingProfile']?.value;
    return isEncodingProfileSnapshot(profile)
      ? profile.targets.find((target) => target.encoderId === encoderId)
      : undefined;
  }

  protected profilePreset(encoder: VideoEncoderDescriptor, presetId: string): string {
    return encoder.presets.find((preset) => preset.id === presetId)?.value ?? presetId;
  }

  protected async selectEncodingProfile(fieldId: string, reference: string): Promise<void> {
    const separator = reference.lastIndexOf('@');
    if (separator <= 0) return;
    this.encodingProfileBusy.set(true);
    this.encodingProfileMessage.set('Chargement de la version sélectionnée…');
    try {
      const version = await this.profileLibrary.version(
        reference.slice(0, separator),
        Number(reference.slice(separator + 1)),
      );
      const snapshot: EncodingProfileSnapshot = {
        id: version.profileId,
        version: version.version,
        name: version.name,
        description: version.description,
        targets: version.targets,
      };
      this.form.controls[fieldId]?.setValue(snapshot);
      this.encodingProfileMessage.set(
        `Profil ${version.name} v${version.version} intégré à la définition du flow.`,
      );
      this.selectFirstCompatibleEncoder();
    } catch {
      this.encodingProfileMessage.set('Impossible de charger cette version de profil.');
    } finally {
      this.encodingProfileBusy.set(false);
    }
  }

  protected onSelectFieldChanged(fieldId: string): void {
    if (fieldId === 'videoCodec' || fieldId === 'encoderMode') this.selectFirstCompatibleEncoder();
  }

  protected textValue(fieldId: string): string {
    const value = this.form.controls[fieldId]?.value;
    return typeof value === 'string' ? value : '';
  }

  protected setTextValue(fieldId: string, value: string): void {
    if (this.busy()) return;
    const control = this.form.controls[fieldId];
    control?.setValue(value);
    control?.markAsDirty();
    control?.markAsTouched();
  }

  protected async selectScriptVersion(fieldId: string, reference: string): Promise<void> {
    if (!reference) {
      this.scriptLoadMessage.set('');
      return;
    }
    const split = reference.lastIndexOf('@');
    if (split <= 0) return;
    this.scriptSelectionBusy.set(true);
    this.scriptLoadMessage.set('Chargement de la version sélectionnée…');
    try {
      const version = await this.scriptLibrary.version(
        reference.slice(0, split),
        Number(reference.slice(split + 1)),
      );
      this.form.controls[fieldId]?.setValue(reference);
      this.form.controls['language']?.setValue(version.language);
      this.form.controls['code']?.setValue(version.code);
      this.form.controls['code']?.markAsDirty();
      this.scriptLoadMessage.set(
        `Code de ${version.name} v${version.version} copié dans cette version du flow.`,
      );
    } catch {
      this.form.controls[fieldId]?.setValue('');
      this.scriptLoadMessage.set('Impossible de charger cette version de script.');
    } finally {
      this.scriptSelectionBusy.set(false);
    }
  }

  private loadScripts(): void {
    this.scriptLoadMessage.set('Chargement des scripts publiés…');
    firstValueFrom(this.scriptLibrary.list()).then(
      (scripts) => {
        this.scripts.set(scripts.filter((script) => script.versions.length > 0));
        this.scriptLoadMessage.set('');
      },
      () => this.scriptLoadMessage.set('Les scripts publiés ne sont pas disponibles.'),
    );
  }

  private loadEncodingProfiles(): void {
    this.encodingProfileMessage.set('Chargement des profils publiés…');
    Promise.all([
      firstValueFrom(this.profileLibrary.list()),
      firstValueFrom(this.profileLibrary.encoders()),
    ]).then(
      ([profiles, encoders]) => {
        this.encodingProfiles.set(profiles.filter((profile) => profile.versions.length > 0));
        this.videoEncoders.set(encoders);
        this.encodingProfileMessage.set('');
        this.selectFirstCompatibleEncoder();
      },
      () => this.encodingProfileMessage.set('Les profils d’encodage ne sont pas disponibles.'),
    );
  }

  private selectFirstCompatibleEncoder(): void {
    const mode = this.form.controls['encoderMode']?.value;
    if (mode !== 'specific') return;
    const options = this.availableProfileEncoders();
    const selected = this.form.controls['encoderId']?.value;
    if (options.some((encoder) => encoder.id === selected)) return;
    this.form.controls['encoderId']?.setValue(options[0]?.id ?? '');
  }
}

function isEncodingProfileSnapshot(value: unknown): value is EncodingProfileSnapshot {
  return (
    typeof value === 'object' &&
    value !== null &&
    'targets' in value &&
    Array.isArray(value.targets)
  );
}
