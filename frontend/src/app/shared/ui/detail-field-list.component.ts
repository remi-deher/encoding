import { Component, computed, input, TemplateRef } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { MatTabsModule } from '@angular/material/tabs';

export interface DetailField {
  readonly section?: string;
  readonly label: string;
  readonly value: string | number | boolean;
}

@Component({
  selector: 'app-detail-field-list',
  imports: [MatTabsModule, NgTemplateOutlet],
  template: `
    @if (tabbed() && sections().length > 1) {
      <mat-tab-group class="detail-tabs" [attr.aria-label]="tabsLabel()">
        @for (section of sections(); track section.title) {
          <mat-tab [label]="section.title || 'Général'">
            @if (sectionTemplates()[section.title]; as template) {
              <ng-container [ngTemplateOutlet]="template" />
            } @else {
              <dl class="detail-field-list tab-fields">
                @for (field of section.fields; track field.label) {
                  <div>
                    <dt>{{ field.label }}</dt>
                    <dd>{{ field.value }}</dd>
                  </div>
                }
              </dl>
            }
          </mat-tab>
        }
      </mat-tab-group>
    } @else {
      <dl class="detail-field-list" [class.detail-field-list_cards]="layout() === 'cards'">
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
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .detail-field-list.detail-field-list_cards {
      margin-bottom: 24px;
    }
    .detail-field-list_cards .detail-section {
      grid-template-columns: repeat(2, minmax(0, 1fr));
      gap: 12px;
    }
    .detail-field-list_cards .detail-section h3 {
      grid-column: 1/-1;
      margin: 4px 0;
    }
    .detail-field-list_cards .detail-section > div {
      padding: 16px;
      border: 1px solid var(--border-subtle);
      border-radius: 12px;
      background: var(--surface);
    }
    .detail-field-list_cards .detail-section dd {
      font-size: 18px;
      font-weight: 600;
    }
    @media (max-width: 600px) {
      .detail-field-list_cards .detail-section {
        grid-template-columns: 1fr;
      }
    }

    .detail-tabs {
      display: block;
    }

    .tab-fields {
      min-height: 120px;
      padding: 18px 4px 4px;
      gap: 9px;
    }

    .detail-field-list {
      margin: 0;
      display: grid;
      gap: 22px;
    }

    .detail-section {
      display: grid;
      gap: 14px;
    }

    .tab-fields > div {
      padding: 12px 14px;
      border: 1px solid var(--border-subtle);
      border-radius: 9px;
      background: var(--surface-muted);
    }

    .detail-section h3 {
      margin: 0;
      color: var(--text-primary);
      font-size: 14px;
      font-weight: 700;
    }

    .detail-field-list div {
      display: grid;
      gap: 5px;
    }

    .detail-field-list dt {
      color: var(--text-muted);
      font-size: 14px;
      font-weight: 700;
      letter-spacing: 0.035em;
      text-transform: uppercase;
    }

    .detail-field-list dd {
      margin: 0;
      color: var(--text-primary);
      font-size: 14px;
      line-height: 1.5;
      overflow-wrap: anywhere;
      white-space: pre-line;
    }
  `,
})
export class DetailFieldListComponent {
  readonly fields = input.required<readonly DetailField[]>();
  readonly layout = input<'list' | 'cards'>('list');
  readonly tabbed = input(false);
  readonly tabsLabel = input('Sections du détail');
  readonly sectionTemplates = input<Readonly<Record<string, TemplateRef<unknown>>>>({});

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
