import { Component, input } from '@angular/core';
import { CdkAccordion, CdkAccordionItem } from '@angular/cdk/accordion';

export type DetailCardTone = 'neutral' | 'accent' | 'success' | 'warning';
export type DetailCardIcon =
  'video' | 'image' | 'audio' | 'plugin' | 'tool' | 'server' | 'activity' | 'system' | 'library';

export interface DetailCardTag {
  readonly label: string;
  readonly tone?: DetailCardTone;
}

export interface DetailCardDetail {
  readonly label: string;
  readonly description?: string;
  readonly tone?: DetailCardTone;
}

export interface DetailCardSection {
  readonly title: string;
  readonly items: readonly DetailCardDetail[];
}

export interface DetailCard {
  readonly id: string;
  readonly title: string;
  readonly subtitle: string;
  readonly icon: DetailCardIcon;
  readonly status?: DetailCardTag;
  readonly tags: readonly DetailCardTag[];
  readonly sections: readonly DetailCardSection[];
  readonly initiallyExpanded?: boolean;
}

@Component({
  selector: 'app-detail-card-grid',
  imports: [CdkAccordion, CdkAccordionItem],
  template: `
    @if (cards().length) {
      <div
        class="detail-card-grid"
        cdkAccordion
        [multi]="true"
        role="group"
        [attr.aria-label]="ariaLabel()"
      >
        @for (card of cards(); track card.id; let index = $index) {
          <article
            class="detail-card"
            [class.detail-card_expanded]="item.expanded"
            cdkAccordionItem
            #item="cdkAccordionItem"
            [expanded]="card.initiallyExpanded ?? false"
          >
            <h3 class="detail-card-heading">
              <button
                class="detail-card-toggle"
                type="button"
                [id]="'detail-card-heading-' + index"
                [attr.aria-expanded]="item.expanded"
                [attr.aria-controls]="'detail-card-content-' + index"
                (click)="item.toggle()"
              >
                <span class="detail-card-icon" aria-hidden="true">
                  @switch (card.icon) {
                    @case ('video') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <rect x="3" y="5" width="18" height="14" rx="2" />
                        <path class="icon-solid" d="m10 9 5 3-5 3z" />
                      </svg>
                    }
                    @case ('image') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <rect x="3" y="4" width="18" height="16" rx="2" />
                        <circle cx="9" cy="9" r="1.5" />
                        <path d="m4 17 5-5 3 3 3-4 5 6" />
                      </svg>
                    }
                    @case ('audio') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path d="M4 10v4h4l5 4V6l-5 4z" />
                        <path d="M16 9a5 5 0 0 1 0 6M18.5 6.5a9 9 0 0 1 0 11" />
                      </svg>
                    }
                    @case ('plugin') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path
                          d="M9 4a2 2 0 1 1 4 0v2h4a2 2 0 0 1 2 2v3h-2a2 2 0 1 0 0 4h2v3a2 2 0 0 1-2 2h-4v-2a2 2 0 1 0-4 0v2H6a2 2 0 0 1-2-2v-4h2a2 2 0 1 0 0-4H4V8a2 2 0 0 1 2-2h3z"
                        />
                      </svg>
                    }
                    @case ('server') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <rect x="4" y="4" width="16" height="6" rx="1.5" />
                        <rect x="4" y="14" width="16" height="6" rx="1.5" />
                        <path d="M8 7h.01M8 17h.01M12 7h5M12 17h5" />
                      </svg>
                    }
                    @case ('activity') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path d="M3 12h4l3-7 4 14 3-7h4" />
                      </svg>
                    }
                    @case ('system') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <rect x="7" y="7" width="10" height="10" rx="2" />
                        <path d="M9 1v4m6-4v4M9 19v4m6-4v4M1 9h4m-4 6h4m14-6h4m-4 6h4" />
                      </svg>
                    }
                    @case ('library') {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path d="M4 5h16v15H4zM8 5v15m8-15v15M2 3h20" />
                      </svg>
                    }
                    @default {
                      <svg viewBox="0 0 24 24" focusable="false">
                        <path d="M4 5h16v3H4zM4 10h16v3H4zM4 15h10v3H4z" />
                      </svg>
                    }
                  }
                </span>
                <span class="detail-card-title-group">
                  <span class="detail-card-title">{{ card.title }}</span>
                  <span class="detail-card-subtitle">{{ card.subtitle }}</span>
                </span>
                @if (card.status; as status) {
                  <span
                    class="detail-card-status"
                    [class]="'detail-card-status detail-card-status_' + status.tone"
                  >
                    {{ status.label }}
                  </span>
                }
                <span
                  class="detail-card-chevron"
                  [class.detail-card-chevron_open]="item.expanded"
                  aria-hidden="true"
                  >⌄</span
                >
              </button>
            </h3>

            @if (card.tags.length) {
              <div class="detail-card-tags" aria-label="Résumé des capacités">
                @for (tag of card.tags; track tag.label) {
                  <span
                    class="detail-card-tag"
                    [class]="'detail-card-tag detail-card-tag_' + tag.tone"
                    >{{ tag.label }}</span
                  >
                }
              </div>
            }

            @if (item.expanded) {
              <div
                class="detail-card-content"
                role="region"
                [id]="'detail-card-content-' + index"
                [attr.aria-labelledby]="'detail-card-heading-' + index"
              >
                @for (section of card.sections; track section.title) {
                  <section class="detail-card-section">
                    <h4>{{ section.title }}</h4>
                    <ul>
                      @for (detail of section.items; track detail.label) {
                        <li>
                          <span>{{ detail.label }}</span>
                          @if (detail.description) {
                            <span
                              class="detail-card-detail"
                              [class]="'detail-card-detail detail-card-detail_' + detail.tone"
                              >{{ detail.description }}</span
                            >
                          }
                        </li>
                      }
                    </ul>
                  </section>
                }
                @if (!card.sections.length) {
                  <p class="detail-card-empty">Aucun détail supplémentaire déclaré.</p>
                }
              </div>
            }
          </article>
        }
      </div>
    } @else {
      <p class="detail-card-empty-state" role="status">{{ emptyMessage() }}</p>
    }
  `,
  styles: `
    :host {
      display: block;
    }
    .detail-card-grid {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 230px), 1fr));
      align-items: start;
      gap: 12px;
    }
    .detail-card {
      min-width: 0;
      overflow: hidden;
      border: 1px solid var(--border-subtle);
      border-radius: 12px;
      background: var(--surface);
      transition:
        border-color 120ms ease,
        box-shadow 120ms ease;
    }
    .detail-card_expanded {
      grid-column: 1 / -1;
      border-color: color-mix(in srgb, var(--accent) 38%, var(--border-subtle));
      box-shadow: 0 3px 12px rgb(32 43 40 / 5%);
    }
    .detail-card-heading {
      margin: 0;
    }
    .detail-card-toggle {
      width: 100%;
      min-height: 68px;
      padding: 11px 12px;
      display: flex;
      align-items: center;
      gap: 10px;
      border: 0;
      background: transparent;
      color: var(--text-primary);
      text-align: left;
      cursor: pointer;
      font: inherit;
    }
    .detail-card-toggle:hover {
      background: var(--surface-muted);
    }
    .detail-card-toggle:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: -2px;
    }
    .detail-card-icon {
      width: 36px;
      height: 36px;
      display: grid;
      place-items: center;
      flex: 0 0 auto;
      border-radius: 10px;
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
    .detail-card-icon svg {
      width: 19px;
      height: 19px;
      fill: none;
      stroke: currentColor;
      stroke-linecap: round;
      stroke-linejoin: round;
      stroke-width: 1.7;
    }
    .detail-card-icon svg .icon-solid {
      fill: currentColor;
      stroke: none;
    }
    .detail-card-title-group {
      min-width: 0;
      flex: 1;
      display: grid;
      gap: 3px;
    }
    .detail-card-title {
      overflow: hidden;
      font-size: 13px;
      font-weight: 700;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .detail-card-subtitle {
      overflow: hidden;
      color: var(--text-muted);
      font-size: 11px;
      font-weight: 400;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .detail-card-status,
    .detail-card-tag {
      max-width: 100%;
      width: fit-content;
      padding: 4px 7px;
      border-radius: 6px;
      background: var(--surface-muted);
      color: var(--text-secondary);
      font-size: 10px;
      font-weight: 600;
      line-height: 1.25;
    }
    .detail-card-status_success,
    .detail-card-tag_success,
    .detail-card-detail_success {
      background: var(--success-soft);
      color: var(--success);
    }
    .detail-card-status_warning,
    .detail-card-tag_warning,
    .detail-card-detail_warning {
      background: var(--warning-soft);
      color: var(--warning);
    }
    .detail-card-status_accent,
    .detail-card-tag_accent,
    .detail-card-detail_accent {
      background: var(--accent-soft);
      color: var(--accent-strong);
    }
    .detail-card-chevron {
      flex: 0 0 auto;
      color: var(--text-muted);
      font-size: 17px;
      transition: transform 120ms ease;
    }
    .detail-card-chevron_open {
      transform: rotate(180deg);
    }
    .detail-card-tags {
      padding: 0 12px 12px 58px;
      display: flex;
      flex-wrap: wrap;
      gap: 6px;
    }
    .detail-card-content {
      padding: 12px;
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(min(100%, 190px), 1fr));
      gap: 12px;
      border-top: 1px solid var(--border-subtle);
    }
    .detail-card-section h4 {
      margin: 0 0 7px;
      color: var(--text-muted);
      font-size: 10px;
      font-weight: 700;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }
    .detail-card-section ul {
      margin: 0;
      padding: 0;
      display: grid;
      gap: 6px;
      list-style: none;
    }
    .detail-card-section li {
      min-width: 0;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 8px;
      font-size: 11px;
    }
    .detail-card-section li > span:first-child {
      overflow-wrap: anywhere;
    }
    .detail-card-detail {
      padding: 3px 6px;
      border-radius: 5px;
      background: var(--surface-muted);
      color: var(--text-secondary);
      font-size: 10px;
      text-align: right;
    }
    .detail-card-empty,
    .detail-card-empty-state {
      margin: 0;
      color: var(--text-muted);
      font-size: 12px;
      line-height: 1.5;
    }
    .detail-card-empty-state {
      padding: 16px;
      border: 1px dashed var(--border-subtle);
      border-radius: 10px;
      background: var(--surface-muted);
    }
    @media (max-width: 600px) {
      .detail-card-grid {
        grid-template-columns: 1fr;
      }
      .detail-card-content {
        grid-template-columns: 1fr;
      }
      .detail-card-status {
        max-width: 86px;
        overflow: hidden;
        text-overflow: ellipsis;
        white-space: nowrap;
      }
    }
    @media (prefers-reduced-motion: reduce) {
      .detail-card,
      .detail-card-chevron {
        transition: none;
      }
    }
  `,
})
export class DetailCardGridComponent {
  readonly cards = input.required<readonly DetailCard[]>();
  readonly ariaLabel = input('Détails');
  readonly emptyMessage = input('Aucun élément à afficher.');
}
