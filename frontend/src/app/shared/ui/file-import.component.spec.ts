import { TestBed } from '@angular/core/testing';
import { FileImportComponent } from './file-import.component';

describe('FileImportComponent', () => {
  it('opens the picker through a Material button and disables it while busy', () => {
    const fixture = TestBed.createComponent(FileImportComponent);
    fixture.componentRef.setInput('label', 'Importer');
    fixture.componentRef.setInput('accept', '.json,.zip');
    fixture.detectChanges();
    const button = fixture.nativeElement.querySelector('button') as HTMLButtonElement;
    const picker = fixture.nativeElement.querySelector('input') as HTMLInputElement;
    const click = vi.spyOn(picker, 'click').mockImplementation(() => {});
    expect(button.textContent).toContain('Importer');
    expect(picker.accept).toBe('.json,.zip');
    button.click();
    expect(click).toHaveBeenCalledOnce();
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    expect(picker.disabled).toBe(true);
    button.click();
    expect(click).toHaveBeenCalledOnce();
  });
});
