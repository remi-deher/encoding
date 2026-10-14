import { flowFieldValidator } from './flow-configuration-controls';
import { flowConfigurationValue } from '../models/flow-configuration';
import { Component, OnChanges, input, output } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
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
            @switch (field.type) {
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
                  <mat-select [formControlName]="field.id">
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
                <mat-form-field appearance="outline" subscriptSizing="dynamic">
                  <mat-label>{{ field.label }}</mat-label>
                  @if (field.multiline) {
                    <textarea
                      matInput
                      rows="3"
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
                    <mat-error>{{ form.controls[field.id].errors?.['configuration'] }}</mat-error>
                  }
                </mat-form-field>
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
            [disabled]="form.invalid || connectionTestBusy()"
            (click)="testConnection()"
          >
            {{ connectionTestBusy() ? 'Test en cours…' : 'Tester la connexion' }}
          </button>
        }
        <button matButton="filled" type="submit" [disabled]="form.invalid || nameControl.invalid">
          {{ submitLabel() }}
        </button>
      </div>
      @if (connectionTestMessage()) {
        <p class="connection-test-result" [class.connection-test-failed]="!connectionTestSuccess()" aria-live="polite">
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
      font-size: 12px;
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
  readonly schema = input.required<FlowComponentConfigurationSchema | undefined>();
  readonly values = input<FlowComponentConfiguration>({});
  readonly configuredSecrets = input<readonly string[]>([]);
  readonly instanceName = input<string | undefined>();
  readonly instanceNameChange = output<string>();
  readonly submitLabel = input('Enregistrer');
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
  protected secretsToClear = new Set<string>();
  protected form: ConfigurationFormGroup = new FormGroup({});
  protected nameControl = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(128)],
  });

  ngOnChanges(): void {
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
  }

  protected save(): void {
    if (this.form.invalid || this.nameControl.invalid) return;
    if (this.instanceName() !== undefined)
      this.instanceNameChange.emit(this.nameControl.value.trim());
    const values = this.form.getRawValue();
    this.saved.emit(values);
    this.savedWithSecrets.emit({ values, clearSecrets: [...this.secretsToClear] });
  }

  protected testConnection(): void {
    if (this.form.invalid || this.connectionTestBusy()) return;
    this.connectionTestRequested.emit(this.form.getRawValue());
  }

  protected setSecretCleared(fieldId: string, cleared: boolean): void {
    const updated = new Set(this.secretsToClear);
    if (cleared) updated.add(fieldId);
    else updated.delete(fieldId);
    this.secretsToClear = updated;
  }
}
