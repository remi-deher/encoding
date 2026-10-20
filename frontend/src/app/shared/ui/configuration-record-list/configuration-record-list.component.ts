import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatCheckboxModule } from '@angular/material/checkbox';
import {
  FlowComponentConfigurationField,
  FlowConfigurationRecord,
} from '../../models/flow-component';

@Component({
  selector: 'app-configuration-record-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatCheckboxModule,
  ],
  template: ` <section [attr.aria-label]="field().label">
    <h3>{{ field().label }}</h3>
    <p>{{ field().description }}</p>
    @for (row of value(); track $index; let rowIndex = $index) {
      <section class="record" [attr.aria-label]="'Règle ' + (rowIndex + 1)">
        <strong>Règle {{ rowIndex + 1 }}</strong>
        @for (child of field().itemFields; track child.id) {
          @if (visible(child, row)) {
            @if (child.type === 'boolean') {
              <mat-checkbox
                [checked]="row[child.id] === true"
                [disabled]="disabled()"
                (change)="change(rowIndex, child, $event.checked)"
                >{{ child.label }}</mat-checkbox
              >
            } @else {
              <mat-form-field appearance="outline" subscriptSizing="dynamic">
                <mat-label>{{ child.label }}</mat-label>
                @if (child.type === 'select') {
                  <mat-select
                    [value]="row[child.id]"
                    [disabled]="disabled()"
                    (selectionChange)="change(rowIndex, child, $event.value)"
                  >
                    @for (option of child.options; track option.value) {
                      <mat-option [value]="option.value">{{ option.label }}</mat-option>
                    }
                  </mat-select>
                } @else {
                  <input
                    matInput
                    [type]="child.type === 'number' ? 'number' : 'text'"
                    [value]="row[child.id] ?? ''"
                    [disabled]="disabled()"
                    (input)="edit(rowIndex, child, $event)"
                  />
                }
                @if (child.description) {
                  <mat-hint>{{ child.description }}</mat-hint>
                }
              </mat-form-field>
            }
          }
        }
        <div class="actions">
          <button
            matButton="text"
            type="button"
            [disabled]="disabled() || rowIndex === 0"
            (click)="move(rowIndex, -1)"
            [attr.aria-label]="'Monter la règle ' + (rowIndex + 1)"
          >
            Monter
          </button>
          <button
            matButton="text"
            type="button"
            [disabled]="disabled() || rowIndex === value().length - 1"
            (click)="move(rowIndex, 1)"
            [attr.aria-label]="'Descendre la règle ' + (rowIndex + 1)"
          >
            Descendre
          </button>
          <button
            matButton="text"
            type="button"
            [disabled]="disabled() || value().length <= (field().minItems ?? 0)"
            (click)="remove(rowIndex)"
            [attr.aria-label]="'Supprimer la règle ' + (rowIndex + 1)"
          >
            Supprimer
          </button>
        </div>
      </section>
    }
    <button
      matButton="outlined"
      type="button"
      [disabled]="disabled() || value().length >= (field().maxItems ?? 64)"
      (click)="add()"
    >
      Ajouter une règle
    </button>
  </section>`,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .record {
      display: grid;
      gap: var(--space-3);
      padding: var(--space-4);
      margin-block: var(--space-3);
      background: var(--surface-container-low);
      border-radius: var(--radius-lg);
    }
    .actions {
      display: flex;
      flex-wrap: wrap;
    }
    p {
      color: var(--text-secondary);
    }
  `,
})
export class ConfigurationRecordListComponent {
  readonly field =
    input.required<Extract<FlowComponentConfigurationField, { type: 'record-list' }>>();
  readonly value = input<readonly FlowConfigurationRecord[]>([]);
  readonly disabled = input(false);
  readonly valueChange = output<readonly FlowConfigurationRecord[]>();
  protected visible(field: FlowComponentConfigurationField, row: FlowConfigurationRecord): boolean {
    return (
      !field.visibleWhenFieldId ||
      (field.visibleWhenEmpty
        ? !row[field.visibleWhenFieldId]
        : row[field.visibleWhenFieldId] === field.visibleWhenValue)
    );
  }
  protected add(): void {
    const row: Record<string, string | number | boolean | null> = {};
    for (const field of this.field().itemFields) {
      const value = field.defaultValue;
      row[field.id] =
        typeof value === 'object'
          ? null
          : (value ?? (field.type === 'boolean' ? false : field.type === 'number' ? null : ''));
    }
    this.valueChange.emit([...this.value(), row]);
  }
  protected edit(index: number, field: FlowComponentConfigurationField, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.change(
      index,
      field,
      field.type === 'number' ? (value === '' ? null : Number(value)) : value,
    );
  }
  protected change(
    index: number,
    field: FlowComponentConfigurationField,
    value: string | number | boolean | null,
  ): void {
    this.valueChange.emit(
      this.value().map((row, i) => (i === index ? { ...row, [field.id]: value } : row)),
    );
  }
  protected remove(index: number): void {
    this.valueChange.emit(this.value().filter((_, i) => i !== index));
  }
  protected move(index: number, offset: number): void {
    const rows = [...this.value()];
    [rows[index], rows[index + offset]] = [rows[index + offset], rows[index]];
    this.valueChange.emit(rows);
  }
}
