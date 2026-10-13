import { Component, computed, input } from '@angular/core';

export interface DetailField {
  readonly section?: string;
  readonly label: string;
  readonly value: string | number;
}

@Component({
  selector: 'app-detail-field-list',
  template: `
    <dl class="detail-field-list">
      @for (section of sections(); track section.title) {
        <section class="detail-section">
          @if (section.title) {
            <h3>{{ section.title }}</h3>
          }
          @for (field of section.fields; track field.label) {
            <div>
              <dt>{{ field.label }}</dt>
              <dd>{{ field.value }}</dd>
            </div>
          }
        </section>
      }
    </dl>
  `,
  styles: `
    .detail-field-list {
      margin: 0;
      display: grid;
      gap: 22px;
    }

    .detail-section {
      display: grid;
      gap: 14px;
    }

    .detail-section h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: 12px;
      font-weight: 700;
    }

    .detail-field-list div {
      display: grid;
      gap: 5px;
    }

    .detail-field-list dt {
      color: var(--text-muted);
      font-size: 11px;
      font-weight: 650;
    }

    .detail-field-list dd {
      margin: 0;
      color: var(--text-primary);
      font-size: 13px;
      overflow-wrap: anywhere;
      white-space: pre-line;
    }
  `,
})
export class DetailFieldListComponent {
  readonly fields = input.required<readonly DetailField[]>();

  protected readonly sections = computed(() => {
    const sections = new Map<string, DetailField[]>();
    for (const field of this.fields()) {
      const title = field.section ?? '';
      const fields = sections.get(title) ?? [];
      fields.push(field);
      sections.set(title, fields);
    }
    return Array.from(sections, ([title, fields]) => ({ title, fields }));
  });
}
