import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Shared layout for a focused configuration section, independent of its domain. */
@Component({
  selector: 'app-configuration-section',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="configuration-section" [attr.aria-label]="title()">
      <header class="section-heading">
        <div>
          <h2>{{ title() }}</h2>
          @if (description()) {
            <p>{{ description() }}</p>
          }
        </div>
        <div class="section-actions"><ng-content select="[section-actions]" /></div>
      </header>
      <ng-content />
    </section>
  `,
  styles: `
    :host {
      display: block;
      min-width: 0;
    }
    .configuration-section {
      padding: var(--space-6);
      background: var(--surface-container-lowest);
      border-radius: var(--radius-xl);
      min-width: 0;
    }
    .section-heading {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      gap: var(--space-4);
      margin-bottom: var(--space-5);
      flex-wrap: wrap;
    }
    h2 {
      margin: 0;
      font-size: var(--font-title-lg);
      font-weight: 650;
    }
    p {
      margin: var(--space-1) 0 0;
      color: var(--text-secondary);
      line-height: 1.6;
    }
    .section-actions {
      display: flex;
      align-items: center;
      gap: var(--space-2);
      flex-wrap: wrap;
    }
    .section-actions:empty {
      display: none;
    }
    @media (max-width: 640px) {
      .configuration-section {
        padding: var(--space-4);
      }
    }
  `,
})
export class ConfigurationSectionComponent {
  readonly title = input.required<string>();
  readonly description = input('');
}
