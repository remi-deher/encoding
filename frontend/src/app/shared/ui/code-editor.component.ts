import {
  AfterViewInit,
  Component,
  ElementRef,
  EventEmitter,
  Input,
  OnChanges,
  OnDestroy,
  Output,
  SimpleChanges,
  ViewChild,
} from '@angular/core';
import { basicSetup } from 'codemirror';
import { Compartment, EditorState } from '@codemirror/state';
import { EditorView } from '@codemirror/view';
import { javascript } from '@codemirror/lang-javascript';
import { python } from '@codemirror/lang-python';

@Component({
  selector: 'app-code-editor',
  template:
    '<div #host class="code-editor" [class.code-editor_roomy]="roomy" [attr.aria-label]="label"></div>',
  styles: [
    `
      :host {
        display: block;
        min-width: 0;
      }
      .code-editor {
        overflow: hidden;
        border: 1px solid var(--border-subtle);
        border-radius: 8px;
      }
      .code-editor:focus-within {
        outline: 2px solid var(--focus-ring, var(--primary));
        outline-offset: 1px;
      }
      :host ::ng-deep .cm-editor {
        min-height: 260px;
        max-height: 560px;
        color: var(--text-primary);
        background: var(--surface);
        font-size: 13px;
      }
      .code-editor_roomy ::ng-deep .cm-editor {
        min-height: 420px;
        max-height: none;
      }
      :host ::ng-deep .cm-scroller {
        overflow: auto;
        font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
      }
      :host ::ng-deep .cm-gutters {
        color: var(--text-muted);
        background: var(--surface-muted);
        border-right: 1px solid var(--border-subtle);
      }
      :host ::ng-deep .cm-activeLineGutter {
        color: var(--text-primary);
      }
      @media (max-width: 600px) {
        :host ::ng-deep .cm-editor {
          min-height: 220px;
          font-size: 12px;
        }
      }
    `,
  ],
})
export class CodeEditorComponent implements AfterViewInit, OnChanges, OnDestroy {
  @Input() value = '';
  @Input() language: 'javascript' | 'python' = 'javascript';
  @Input() disabled = false;
  @Input() roomy = false;
  @Input() label = 'Code du script';
  @Output() readonly valueChange = new EventEmitter<string>();
  @ViewChild('host', { static: true }) private readonly host!: ElementRef<HTMLDivElement>;

  private view?: EditorView;
  private readonly languageSlot = new Compartment();
  private readonly editableSlot = new Compartment();
  private syncing = false;

  ngAfterViewInit(): void {
    this.view = new EditorView({
      parent: this.host.nativeElement,
      state: EditorState.create({
        doc: this.value,
        extensions: [
          basicSetup,
          this.languageSlot.of(this.languageExtension()),
          this.editableSlot.of(EditorView.editable.of(!this.disabled)),
          EditorView.theme({
            '&': { backgroundColor: 'var(--surface)', color: 'var(--text-primary)' },
            '.cm-content': { caretColor: 'var(--text-primary)' },
            '.cm-cursor, .cm-dropCursor': { borderLeftColor: 'var(--text-primary)' },
            '&.cm-focused .cm-selectionBackground, .cm-selectionBackground': {
              backgroundColor: 'var(--selection-subtle, #dce6ff)',
            },
          }),
          EditorView.updateListener.of((update) => {
            if (!this.syncing && update.docChanged)
              this.valueChange.emit(update.state.doc.toString());
          }),
        ],
      }),
    });
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (!this.view) return;
    if (changes['value'] && this.view.state.doc.toString() !== this.value) {
      this.syncing = true;
      this.view.dispatch({
        changes: { from: 0, to: this.view.state.doc.length, insert: this.value ?? '' },
      });
      this.syncing = false;
    }
    if (changes['language'])
      this.view.dispatch({ effects: this.languageSlot.reconfigure(this.languageExtension()) });
    if (changes['disabled'])
      this.view.dispatch({
        effects: this.editableSlot.reconfigure(EditorView.editable.of(!this.disabled)),
      });
  }

  ngOnDestroy(): void {
    this.view?.destroy();
  }

  private languageExtension() {
    return this.language === 'python' ? python() : javascript();
  }
}
