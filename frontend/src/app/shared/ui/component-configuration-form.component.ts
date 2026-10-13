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
            }
          }
        </div>
      } @else {
        <p class="empty-configuration">Ce composant ne demande aucun réglage.</p>
      }

      <div class="form-actions">
        <button matButton="text" type="button" (click)="cancelled.emit()">Annuler</button>
        <button matButton="filled" type="submit" [disabled]="form.invalid || nameControl.invalid">
          {{ submitLabel() }}
        </button>
      </div>
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
  readonly instanceName = input<string | undefined>();
  readonly instanceNameChange = output<string>();
  readonly submitLabel = input('Enregistrer');
  readonly saved = output<FlowComponentConfiguration>();
  readonly cancelled = output<void>();

  protected fields: readonly FlowComponentConfigurationField[] = [];
  protected form: ConfigurationFormGroup = new FormGroup({});
  protected nameControl = new FormControl('', {
    nonNullable: true,
    validators: [Validators.maxLength(128)],
  });

  ngOnChanges(): void {
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
    this.saved.emit(this.form.getRawValue());
  }
}
