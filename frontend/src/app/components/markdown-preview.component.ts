import { CommonModule } from '@angular/common';
import { Component, input } from '@angular/core';
import { MarkdownPipe } from '../markdown.pipe';

@Component({
  selector: 'app-markdown-preview',
  standalone: true,
  imports: [CommonModule, MarkdownPipe],
  template: `
    <div class="markdown-preview-container markdown-body" [innerHTML]="content() | markdown"></div>
  `,
  styles: [
    `
      .markdown-preview-container {
        padding: 16px;
        overflow-y: auto;
        max-height: 100%;
        color: var(--base-12);
      }
    `,
  ],
})
export class MarkdownPreviewComponent {
  content = input.required<string>();
}
