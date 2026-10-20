import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { ComponentConfigurationFormComponent } from './component-configuration-form.component';

describe('Flow inspector progressive disclosure', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient()] }));

  it('keeps a condition editor in the primary settings even when the schema does not mark it inline', async () => {
    const fixture = TestBed.createComponent(ComponentConfigurationFormComponent);
    fixture.componentRef.setInput('progressiveDisclosure', true);
    fixture.componentRef.setInput('schema', {
      version: 1,
      fields: [
        { id: 'rules', label: 'Règles du média', type: 'condition-tree', conditionFields: [] },
        { id: 'note', label: 'Note', type: 'text' },
      ],
    });
    await fixture.whenStable();
    const editor = fixture.nativeElement.querySelector('app-condition-tree-editor') as HTMLElement;
    expect(editor).not.toBeNull();
    expect(editor.closest('details')).toBeNull();
    expect(fixture.nativeElement.querySelector('details').textContent).toContain('Note');
  });

  it('reveals an invalid advanced option so a disabled submit can be corrected', async () => {
    const fixture = TestBed.createComponent(ComponentConfigurationFormComponent);
    fixture.componentRef.setInput('progressiveDisclosure', true);
    fixture.componentRef.setInput('schema', {
      version: 1,
      fields: [{ id: 'limit', label: 'Limite', type: 'number', min: 1, max: 10 }],
    });
    fixture.componentRef.setInput('values', { limit: 99 });
    await fixture.whenStable();
    const advanced = fixture.nativeElement.querySelector('details') as HTMLDetailsElement;
    expect(advanced.open).toBe(true);
    const input = advanced.querySelector('input')!;
    input.value = '5';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(
      (fixture.nativeElement.querySelector('button[type=submit]') as HTMLButtonElement).disabled,
    ).toBe(false);
  });
});
