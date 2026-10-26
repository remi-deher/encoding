import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  effect,
  inject,
  signal,
} from '@angular/core';

/** Gabarit commun des pages organisées autour d’une sous-navigation. */
@Component({
  selector: 'app-subnav-template',
  host: { '[class.is-stuck]': 'navigationStuck()' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="subnav-template">
      <div class="subnav-template-info"><ng-content select="[subnav-info]" /></div>
      <div class="subnav-template-navigation"><ng-content select="app-subnav" /></div>
      <div class="subnav-template-actions"><ng-content select="[subnav-actions]" /></div>
    </div>
  `,
  styleUrl: './subnav-template.component.scss',
})
export class SubnavTemplateComponent {
  private readonly host = inject(ElementRef<HTMLElement>);
  protected readonly navigationStuck = signal(false);

  constructor() {
    effect((onCleanup) => {
      const host = this.host.nativeElement;
      if (typeof window === 'undefined') return;

      const scrollRegion =
        (host.closest('.page-transition-region') as HTMLElement | null) ?? window;
      const updateStuckState = () => {
        const stickyTop = Number.parseFloat(window.getComputedStyle(host).top);
        const stuck = host.getBoundingClientRect().top <= stickyTop + 1;
        if (stuck !== this.navigationStuck()) this.navigationStuck.set(stuck);
      };

      scrollRegion.addEventListener('scroll', updateStuckState, { passive: true });
      window.addEventListener('resize', updateStuckState, { passive: true });
      updateStuckState();

      onCleanup(() => {
        scrollRegion.removeEventListener('scroll', updateStuckState);
        window.removeEventListener('resize', updateStuckState);
      });
    });
  }
}
