import { ChangeDetectionStrategy, Component, ElementRef, signal, viewChild } from '@angular/core';
import { bootstrapApplication } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { OverlayModule } from '@angular/cdk/overlay';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatMenuModule } from '@angular/material/menu';
import { MatChipsModule } from '@angular/material/chips';
import { ThemeToggleComponent } from '../../src/app/core/theme/theme-toggle.component';
import { AiAssistantModelOption } from '../../src/app/shared/models/ai-tools';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    OverlayModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatMenuModule,
    MatChipsModule,
    ThemeToggleComponent,
  ],
  templateUrl: './mockup.html',
  styleUrl: './mockup.scss',
})
class WidgetMockup {
  readonly open = signal(false);
  readonly text = signal('');
  readonly model = signal('codex-demo');
  readonly effort = signal<string | null>(null);
  readonly resource = signal<string | null>(null);
  readonly messages = signal<{ role: string; text: string }[]>([]);
  // Fixture only. Production consumes AiAssistantOptions from the backend.
  readonly models: readonly AiAssistantModelOption[] = [
    {
      provider: 'codex-cli',
      model: 'codex-demo',
      label: 'GPT 6.1',
      reasoningEfforts: ['low', 'medium', 'high'],
      reasoningKnown: true,
    },
    {
      provider: 'claude-cli',
      model: 'opus-demo',
      label: 'Claude Opus',
      reasoningEfforts: ['medium', 'high', 'max'],
      reasoningKnown: true,
    },
    {
      provider: 'claude-cli',
      model: 'sonnet-demo',
      label: 'Claude Sonnet',
      reasoningEfforts: ['low', 'medium', 'high'],
      reasoningKnown: true,
    },
  ];
  readonly launcher = viewChild('launcher', { read: ElementRef });
  readonly input = viewChild<ElementRef<HTMLTextAreaElement>>('messageInput');
  readonly thread = viewChild<ElementRef<HTMLElement>>('thread');
  readonly position = [
    {
      originX: 'end' as const,
      originY: 'top' as const,
      overlayX: 'end' as const,
      overlayY: 'bottom' as const,
      offsetY: -12,
      offsetX: 16,
    },
  ];
  toggle() {
    if (this.open()) this.close();
    else this.open.set(true);
  }
  close() {
    this.open.set(false);
    this.launcher()?.nativeElement.focus();
  }
  focusInput() {
    this.input()?.nativeElement.focus();
  }
  selectModel(value: string) {
    this.model.set(value);
    this.effort.set(null);
  }
  efforts() {
    return this.models.find((option) => option.model === this.model())?.reasoningEfforts ?? [];
  }
  effortLabel(value: string) {
    return (
      ({ low: 'Faible', medium: 'Moyen', high: 'Élevé', max: 'Maximum' } as Record<string, string>)[
        value
      ] ?? value
    );
  }
  send() {
    const text = this.text().trim();
    if (!text) return;
    this.messages.update((messages) => [
      ...messages,
      { role: 'Vous', text },
      {
        role: 'Assistant',
        text: 'Voici où apparaîtra la réponse de l’assistant. Cet aperçu ne contacte aucun fournisseur IA.',
      },
    ]);
    this.text.set('');
    setTimeout(() => {
      const element = this.thread()?.nativeElement;
      element?.scrollTo({ top: element.scrollHeight });
      this.focusInput();
    });
  }
  keydown(event: KeyboardEvent) {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      this.send();
    }
  }
}
bootstrapApplication(WidgetMockup).catch(console.error);
