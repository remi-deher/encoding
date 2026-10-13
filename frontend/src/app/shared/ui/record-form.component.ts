import { Component, OnInit, input, output } from '@angular/core';
import { FormControl, FormGroup, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

export interface RecordFormField {
  readonly id: string;
  readonly label: string;
  readonly type: 'text' | 'select';
  readonly required?: boolean;
  readonly maxLength?: number;
  readonly filterOptionsByFieldId?: string;
  readonly options?: readonly RecordFormOption[];
}

export interface RecordFormOption {
  readonly value: string;
  readonly label: string;
  readonly mediaTypes?: readonly string[];
}

export interface RecordFormData {
  readonly title: string;
  readonly description: string;
  readonly submitLabel: string;
  readonly reviewTitle?: string;
  readonly fields: readonly RecordFormField[];
  readonly initialValues?: Readonly<Record<string, string>>;
}

@Component({
  selector: 'app-record-form',
  imports: [MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, ReactiveFormsModule],
  template: `
    <section class="record-form" [attr.aria-label]="data().title">
      @if (!reviewing) {
        <form [formGroup]="form" (ngSubmit)="review()">
          @for (field of data().fields; track field.id) {
            @if (field.type === 'select') {
              <mat-form-field appearance="outline">
                <mat-label>{{ field.label }}</mat-label>
                <mat-select [formControlName]="field.id" (selectionChange)="onSelectChange(field)">
                  @for (option of optionsFor(field); track option.value) {
                    <mat-option [value]="option.value">{{ option.label }}</mat-option>
                  }
                </mat-select>
                @if (field.filterOptionsByFieldId) {
                  <mat-hint>Les choix proposés dépendent du type de média.</mat-hint>
                }
              </mat-form-field>
            } @else {
              <mat-form-field appearance="outline">
                <mat-label>{{ field.label }}</mat-label>
                <input matInput [formControlName]="field.id" [attr.maxlength]="field.maxLength ?? null" />
              </mat-form-field>
            }
          }
          <div class="form-actions">
            <button matButton="text" type="button" (click)="cancelled.emit()">Annuler</button>
            <button matButton="filled" type="submit" [disabled]="!canReview()">Vérifier</button>
          </div>
        </form>
      } @else {
        <h3>{{ data().reviewTitle ?? 'Vérifier la définition' }}</h3>
        <dl class="summary">
          @for (field of data().fields; track field.id) {
            <div><dt>{{ field.label }}</dt><dd>{{ displayValue(field.id) }}</dd></div>
          }
        </dl>
        <div class="form-actions">
          <button matButton="text" type="button" (click)="reviewing = false">Modifier</button>
          <button matButton="filled" type="button" (click)="save()">{{ data().submitLabel }}</button>
        </div>
      }
    </section>
  `,
  styles: `
    .record-form { display: grid; gap: 16px; }
    form { display: grid; gap: 8px; }
    mat-form-field { width: 100%; }
    h3 { margin: 0; color: var(--text-primary); font-size: 14px; }
    .summary { display: grid; gap: 12px; margin: 0; }
    .summary div { display: grid; gap: 4px; }
    dt { color: var(--text-muted); font-size: 11px; font-weight: 700; text-transform: uppercase; }
    dd { margin: 0; color: var(--text-primary); }
    .form-actions { display: flex; justify-content: flex-end; gap: 8px; padding-top: 8px; }
  `,
})
export class RecordFormComponent implements OnInit {
  readonly data = input.required<RecordFormData>();
  readonly submitted = output<Record<string, string>>();
  readonly cancelled = output<void>();
  protected readonly form = new FormGroup<Record<string, FormControl<string>>>({});
  protected reviewing = false;

  ngOnInit(): void {
    for (const field of this.data().fields) {
      const validators = [];
      if (field.required) validators.push(Validators.required);
      if (field.maxLength) validators.push(Validators.maxLength(field.maxLength));
      this.form.addControl(field.id, new FormControl(this.data().initialValues?.[field.id] ?? '', { nonNullable: true, validators }));
    }
  }

  protected save(): void {
    if (this.form.valid) this.submitted.emit(this.form.getRawValue());
  }

  protected review(): void {
    if (this.canReview()) this.reviewing = true;
  }

  protected canReview(): boolean {
    if (!this.form.valid) return false;
    return this.data().fields.every((field) => {
      if (!field.filterOptionsByFieldId) return true;
      const option = field.options?.find((item) => item.value === this.form.controls[field.id]?.value);
      if (!option) return false;
      if (!option.mediaTypes?.length) return true;
      return option.mediaTypes.includes(this.form.controls[field.filterOptionsByFieldId]?.value ?? '');
    });
  }

  protected optionsFor(field: RecordFormField): readonly RecordFormOption[] {
    const filterFieldId = field.filterOptionsByFieldId;
    if (!filterFieldId) return field.options ?? [];
    const mediaType = this.form.controls[filterFieldId]?.value;
    return (field.options ?? []).filter((option) => !option.mediaTypes?.length || option.mediaTypes.includes(mediaType ?? ''));
  }

  protected onSelectChange(field: RecordFormField): void {
    for (const dependent of this.data().fields.filter((item) => item.filterOptionsByFieldId === field.id)) {
      const value = this.form.controls[dependent.id]?.value;
      const option = dependent.options?.find((item) => item.value === value);
      if (!option?.mediaTypes?.length || option.mediaTypes.includes(this.form.controls[field.id]?.value ?? '')) continue;
      this.form.controls[dependent.id]?.setValue('');
    }
  }

  protected displayValue(fieldId: string): string {
    const field = this.data().fields.find((item) => item.id === fieldId);
    const value = this.form.controls[fieldId]?.value ?? '';
    return field?.options?.find((option) => option.value === value)?.label ?? value;
  }
}
