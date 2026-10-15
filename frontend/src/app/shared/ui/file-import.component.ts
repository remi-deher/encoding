import { Component, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';

@Component({
  selector: 'app-file-import',
  imports: [MatButtonModule],
  template: `
    <input
      #picker
      hidden
      type="file"
      [accept]="accept()"
      [disabled]="busy()"
      (change)="select(picker)"
    />
    <button matButton="outlined" type="button" [disabled]="busy()" (click)="picker.click()">
      {{ busy() ? 'Lecture en cours…' : label() }}
    </button>
    @if (description()) {
      <p>{{ description() }}</p>
    }
  `,
  styles: `
    p {
      color: var(--text-secondary);
      font-size: 14px;
      line-height: 1.5;
    }
  `,
})
export class FileImportComponent {
  readonly accept = input('');
  readonly busy = input(false);
  readonly label = input('Importer un fichier');
  readonly description = input('');
  readonly selected = output<File>();
  protected select(input: HTMLInputElement): void {
    const file = input.files?.[0];
    if (file) this.selected.emit(file);
    input.value = '';
  }
}
