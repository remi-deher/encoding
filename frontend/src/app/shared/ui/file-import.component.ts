import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { MatButtonModule } from '@angular/material/button';

@Component({
  changeDetection: ChangeDetectionStrategy.OnPush,
  selector: 'app-file-import',
  imports: [MatButtonModule, MatIconModule],
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
      <mat-icon aria-hidden="true">upload_file</mat-icon>
      {{ busy() ? 'Lecture en cours…' : label() }}
    </button>
    @if (description()) {
      <p>{{ description() }}</p>
    }
  `,
  styles: `
    p {
      color: var(--text-secondary);
      font-size: var(--font-body);
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
