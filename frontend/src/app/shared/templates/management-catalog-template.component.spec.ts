import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ManagementCatalogTemplateComponent } from './management-catalog-template.component';

@Component({
  imports: [ManagementCatalogTemplateComponent],
  template: `
    <app-management-catalog-template
      title="Test"
      searchLabel="Rechercher"
      [searchLayout]="layout"
      [filterCount]="1"
    >
      <span catalogue-filters class="probe">filtre</span>
    </app-management-catalog-template>
  `,
})
class HostComponent {
  layout: 'end' | 'centered' | 'centered-sheet' = 'end';
}

describe('ManagementCatalogTemplateComponent search layout', () => {
  function render(layout: HostComponent['layout']) {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.layout = layout;
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('keeps filters beside the search bar by default', () => {
    const el = render('end');
    expect(el.querySelector('.probe')).toBeTruthy();
    expect(el.querySelector('.search-field-panel')).toBeNull();
    expect(el.querySelector('.is-centered')).toBeNull();
  });

  it('centers the bar and puts filters below it', () => {
    const el = render('centered');
    expect(el.querySelector('.is-centered')).toBeTruthy();
    expect(el.querySelector('.probe')).toBeTruthy();
    expect(el.querySelector('.search-field-panel')).toBeNull();
  });

  it('moves filters into the sheet opened from the bar', () => {
    const el = render('centered-sheet');
    expect(el.querySelector('.search-field-panel .probe')).toBeTruthy();
    expect(el.querySelector('.search-field-filters')?.textContent).toContain('1');
  });
});
