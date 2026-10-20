import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { ConfigurationReferenceFieldComponent } from './configuration-reference-field.component';

describe('Configuration references', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }),
  );
  afterEach(() => TestBed.inject(HttpTestingController).verify());
  it('preserves an unavailable worker without silently selecting another', async () => {
    const fixture = TestBed.createComponent(ConfigurationReferenceFieldComponent);
    fixture.componentRef.setInput('field', {
      id: 'targetNodeId',
      type: 'text',
      label: 'Worker',
      editor: 'node',
    });
    fixture.componentRef.setInput('value', 'missing-worker');
    const changed = vi.fn();
    fixture.componentInstance.valueChange.subscribe(changed);
    fixture.detectChanges();
    TestBed.inject(HttpTestingController).expectOne('/api/v1/nodes').flush([]);
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('input').value).toBe('missing-worker');
    expect(changed).not.toHaveBeenCalled();
    expect(fixture.nativeElement.textContent).toContain('Aucune ressource observée');
  });
  it('adds languages without replacing existing historical or unknown codes', async () => {
    const fixture = TestBed.createComponent(ConfigurationReferenceFieldComponent);
    fixture.componentRef.setInput('field', {
      id: 'languages',
      type: 'text',
      label: 'Langues',
      editor: 'languages',
    });
    fixture.componentRef.setInput('value', 'fre,xyz');
    const changed = vi.fn();
    fixture.componentInstance.valueChange.subscribe(changed);
    await fixture.whenStable();
    const input = fixture.nativeElement.querySelector('input');
    input.value = 'jpn';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    expect(changed).toHaveBeenCalledWith('fre,xyz,jpn');
  });
});
