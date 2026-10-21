import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { ComponentConfigurationFormComponent } from '../component-configuration-form.component';

describe('Declarative record lists', () => {
  beforeEach(() => TestBed.configureTestingModule({ providers: [provideHttpClient()] }));
  it('adds, edits and submits a structured rule using the common form', async () => {
    const fixture = TestBed.createComponent(ComponentConfigurationFormComponent);
    fixture.componentRef.setInput('schema', {
      version: 1,
      fields: [
        {
          id: 'rules',
          type: 'record-list',
          label: 'Règles audio',
          minItems: 1,
          maxItems: 2,
          itemFields: [
            { id: 'languages', type: 'text', label: 'Langues', defaultValue: 'fra' },
            {
              id: 'codec',
              type: 'select',
              label: 'Codec',
              defaultValue: 'ac3',
              options: [{ value: 'ac3', label: 'AC-3' }],
            },
          ],
        },
      ],
    });
    const saved = vi.fn();
    fixture.componentInstance.saved.subscribe(saved);
    await fixture.whenStable();
    expect(fixture.nativeElement.querySelector('button[type=submit]').disabled).toBe(true);
    const add = [...fixture.nativeElement.querySelectorAll('button')].find((button) =>
      (button as HTMLElement).textContent?.includes('Ajouter une règle'),
    ) as HTMLButtonElement;
    add.click();
    await fixture.whenStable();
    const input = fixture.nativeElement.querySelector('input');
    input.value = 'jpn';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    fixture.nativeElement.querySelector('form').dispatchEvent(new Event('submit'));
    expect(saved).toHaveBeenCalledWith({ rules: [{ languages: 'jpn', codec: 'ac3' }] });
  });
});
