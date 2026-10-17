import { TestBed } from '@angular/core/testing';
import { DetailDrawerComponent } from './detail-drawer';

describe('DetailDrawerComponent', () => {
  let sidebar: HTMLElement;

  beforeEach(() => {
    sidebar = document.createElement('aside');
    sidebar.className = 'application-sidebar';
    document.body.appendChild(sidebar);
  });

  afterEach(() => {
    sidebar.remove();
    document.body.classList.remove('detail-drawer-open');
  });

  function render(inputs: Record<string, unknown> = {}) {
    const fixture = TestBed.createComponent(DetailDrawerComponent);
    for (const [key, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(key, value);
    }
    fixture.detectChanges();
    return { fixture, element: fixture.nativeElement as HTMLElement };
  }

  it('toggles detail-drawer-open class on body and inert on sidebar when modal is true and opened', () => {
    const { fixture } = render({ modal: true, opened: false });
    expect(document.body.classList.contains('detail-drawer-open')).toBe(false);
    expect(sidebar.hasAttribute('inert')).toBe(false);

    fixture.componentRef.setInput('opened', true);
    fixture.detectChanges();

    expect(document.body.classList.contains('detail-drawer-open')).toBe(true);
    expect(sidebar.hasAttribute('inert')).toBe(true);

    fixture.componentRef.setInput('opened', false);
    fixture.detectChanges();

    expect(document.body.classList.contains('detail-drawer-open')).toBe(false);
    expect(sidebar.hasAttribute('inert')).toBe(false);
  });

  it('does not toggle class or inert when modal is false', () => {
    const { fixture } = render({ modal: false, opened: true });
    expect(document.body.classList.contains('detail-drawer-open')).toBe(false);
    expect(sidebar.hasAttribute('inert')).toBe(false);
  });

  it('removes class and inert upon component destruction', () => {
    const { fixture } = render({ modal: true, opened: true });
    expect(document.body.classList.contains('detail-drawer-open')).toBe(true);
    expect(sidebar.hasAttribute('inert')).toBe(true);

    fixture.destroy();

    expect(document.body.classList.contains('detail-drawer-open')).toBe(false);
    expect(sidebar.hasAttribute('inert')).toBe(false);
  });
});
