import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { StatusBadgeComponent, StatusTone } from './status-badge.component';

export interface ResourceSheetBadge {
  readonly label: string;
  readonly tone?: StatusTone;
}

export interface ResourceSheetMeter {
  readonly value: number;
  readonly max: number;
  readonly label: string;
  /** Dernière valeur connue : la barre n'est pas présentée comme une occupation actuelle. */
  readonly stale?: boolean;
}

export interface ResourceSheetMetric {
  readonly label: string;
  /** Valeur lisible ; `unavailable` affiche une valeur inconnue sans la remplacer par zéro. */
  readonly value: string;
  readonly detail?: string;
  readonly unavailable?: boolean;
  readonly stale?: boolean;
  readonly meter?: ResourceSheetMeter;
}

export interface ResourceSheetItem {
  readonly label: string;
  readonly detail?: string;
  readonly badges?: readonly ResourceSheetBadge[];
}

export interface ResourceSheetField {
  readonly label: string;
  readonly value: string;
  readonly detail?: string;
  readonly mono?: boolean;
}

export interface ResourceSheetSection {
  readonly id: string;
  readonly title: string;
  readonly caption?: string;
  readonly notice?: string;
  readonly items?: readonly ResourceSheetItem[];
  readonly metrics?: readonly ResourceSheetMetric[];
  readonly fields?: readonly ResourceSheetField[];
  readonly action?: { readonly id: string; readonly label: string };
}

export interface ResourceSheetNotice {
  readonly tone: 'warning' | 'danger' | 'accent';
  readonly title: string;
  readonly description: string;
}

/**
 * Fiche d'une ressource unique déjà ouverte (docs/design/interface-resource-layout.md) : en-tête, avis, puis sections
 * connectées M3 Expressive. Les définitions métier fournissent les données ; aucun chargement ici.
 */
@Component({
  selector: 'app-resource-sheet',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, StatusBadgeComponent],
  template: `
    <div class="sheet-stack">
      @for (notice of notices(); track notice.title) {
        <div class="sheet-notice" [class]="'sheet-notice tone-' + notice.tone" role="status">
          <strong>{{ notice.title }}</strong>
          <span>{{ notice.description }}</span>
        </div>
      }
      <article class="sheet" [attr.aria-label]="ariaLabel()">
        <header class="sheet-head">
          <div>
            <h2>{{ title() }}</h2>
            @if (subtitle()) {
              <p>{{ subtitle() }}</p>
            }
          </div>
        </header>
        @for (section of sections(); track section.id) {
          <section class="sheet-section" [attr.aria-labelledby]="'sheet-' + section.id">
            <h3 [id]="'sheet-' + section.id">
              <span>{{ section.title }}</span>
              @if (section.caption) {
                <small>{{ section.caption }}</small>
              }
              @if (section.action; as action) {
                <button matButton type="button" (click)="actionSelected.emit(action.id)">
                  {{ action.label }}
                </button>
              }
            </h3>
            @if (section.notice) {
              <p class="sheet-hint">{{ section.notice }}</p>
            }
            @if (section.items?.length) {
              <ul class="sheet-items">
                @for (item of section.items; track item.label) {
                  <li>
                    <div class="sheet-item-line">
                      <strong>{{ item.label }}</strong>
                      @for (badge of item.badges ?? []; track badge.label) {
                        <app-status-badge [label]="badge.label" [tone]="badge.tone ?? 'neutral'" />
                      }
                    </div>
                    @if (item.detail) {
                      <span class="sheet-detail">{{ item.detail }}</span>
                    }
                  </li>
                }
              </ul>
            }
            @if (section.metrics?.length) {
              <dl class="sheet-metrics">
                @for (metric of section.metrics; track metric.label) {
                  <div class="sheet-metric">
                    <dt>{{ metric.label }}</dt>
                    <dd class="sheet-value" [class.is-unavailable]="metric.unavailable">
                      {{ metric.value }}
                    </dd>
                    @if (metric.meter; as meter) {
                      <div
                        class="sheet-bar"
                        [class.is-stale]="meter.stale"
                        role="progressbar"
                        [attr.aria-valuemin]="0"
                        [attr.aria-valuemax]="meter.max"
                        [attr.aria-valuenow]="meter.value"
                        [attr.aria-label]="meter.label"
                      >
                        <span [style.width.%]="percent(meter.value, meter.max)"></span>
                      </div>
                    }
                    @if (metric.detail) {
                      <span class="sheet-detail" [class.is-stale]="metric.stale">{{
                        metric.detail
                      }}</span>
                    }
                  </div>
                }
              </dl>
            }
            @if (section.fields?.length) {
              <dl class="sheet-metrics">
                @for (field of section.fields; track field.label) {
                  <div class="sheet-metric">
                    <dt>{{ field.label }}</dt>
                    <dd class="sheet-field" [class.is-mono]="field.mono">{{ field.value }}</dd>
                    @if (field.detail) {
                      <span class="sheet-detail">{{ field.detail }}</span>
                    }
                  </div>
                }
              </dl>
            }
          </section>
        }
      </article>
    </div>
  `,
  styleUrl: './resource-sheet.component.scss',
})
export class ResourceSheetComponent {
  readonly ariaLabel = input.required<string>();
  readonly title = input.required<string>();
  readonly subtitle = input<string>('');
  readonly notices = input<readonly ResourceSheetNotice[]>([]);
  readonly sections = input.required<readonly ResourceSheetSection[]>();
  readonly actionSelected = output<string>();

  protected percent(value: number, max: number): number {
    return max > 0 ? Math.min(100, Math.max(0, (value / max) * 100)) : 0;
  }
}
