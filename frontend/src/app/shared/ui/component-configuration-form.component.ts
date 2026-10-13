import { Component, OnChanges, input, output } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
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

type ConfigurationFormGroup = FormGroup<Record<string, FormControl<FlowComponentConfigurationValue>>>;

@Component({
  selector: 'app-component-configuration-form',
  imports: [MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, ReactiveFormsModule],
  template: `
    <form class="configuration-form" [formGroup]="form" (ngSubmit)="save()">
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
                <mat-form-field appearance="outline">
                  <mat-label>{{ field.label }}</mat-label>
                  <mat-select [formControlName]="field.id">
                    @for (option of field.options; track option.value) {
                      <mat-option [value]="option.value">{{ option.label }}</mat-option>
                    }
                  </mat-select>
                  @if (field.description) { <mat-hint>{{ field.description }}</mat-hint> }
                </mat-form-field>
              }
              @case ('number') {
                <mat-form-field appearance="outline">
                  <mat-label>{{ field.label }}</mat-label>
                  <input matInput type="number" [formControlName]="field.id" [attr.min]="field.min ?? null" [attr.max]="field.max ?? null" [attr.step]="field.step ?? null" />
                  @if (field.description) { <mat-hint>{{ field.description }}</mat-hint> }
                </mat-form-field>
              }
              @case ('text') {
                <mat-form-field appearance="outline">
                  <mat-label>{{ field.label }}</mat-label>
                  @if (field.multiline) {
                    <textarea matInput rows="3" [formControlName]="field.id" [placeholder]="field.placeholder ?? ''"></textarea>
                  } @else {
                    <input matInput type="text" [formControlName]="field.id" [placeholder]="field.placeholder ?? ''" />
                  }
                  @if (field.description) { <mat-hint>{{ field.description }}</mat-hint> }
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
        <button matButton="filled" type="submit" [disabled]="form.invalid">Enregistrer</button>
      </div>
    </form>
  `,
  styles: `
    :host { display: block; }
    .configuration-fields { display: grid; gap: 8px; }
    mat-form-field { width: 100%; }
    .boolean-field { padding: 8px 0; }
    .field-description { margin: 4px 0 0 32px; color: var(--text-muted); font-size: 12px; }
    .empty-configuration { margin: 0; color: var(--text-secondary); }
    .form-actions { display: flex; justify-content: flex-end; gap: 8px; padding-top: 16px; }
    @media (max-width: 600px) {
      .form-actions { position: sticky; bottom: 0; margin-inline: -16px; padding: 12px 16px; background: var(--surface); border-top: 1px solid var(--border-subtle); }
      .form-actions button { flex: 1; }
    }
  `,
})
export class ComponentConfigurationFormComponent implements OnChanges {
  readonly schema = input.required<FlowComponentConfigurationSchema | undefined>();
  readonly values = input<FlowComponentConfiguration>({});
  readonly saved = output<FlowComponentConfiguration>();
  readonly cancelled = output<void>();

  protected fields: readonly FlowComponentConfigurationField[] = [];
  protected form: ConfigurationFormGroup = new FormGroup({});

  ngOnChanges(): void {
    this.fields = this.schema()?.fields ?? [];
    const controls: Record<string, FormControl<FlowComponentConfigurationValue>> = {};
    for (const field of this.fields) {
      const validators: ValidatorFn[] = [];
      if (field.required) validators.push(Validators.required);
      if (field.type === 'number' && field.min !== undefined) validators.push(Validators.min(field.min));
      if (field.type === 'number' && field.max !== undefined) validators.push(Validators.max(field.max));
      const fallback = field.type === 'boolean' ? false : field.type === 'number' ? null : '';
      controls[field.id] = new FormControl(this.values()[field.id] ?? field.defaultValue ?? fallback, { validators });
    }
    this.form = new FormGroup(controls);
  }

  protected save(): void {
    if (this.form.invalid) return;
    this.saved.emit(this.form.getRawValue());
  }
}
