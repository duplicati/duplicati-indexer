import { CommonModule } from '@angular/common';
import { Component, input, OnChanges, signal } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';

// @ts-nocheck
import hljs from 'highlight.js';

@Component({
  selector: 'app-code-preview',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="code-preview-container">
      <pre><code [innerHTML]="highlightedCode()"></code></pre>
    </div>
  `,
  styles: [
    `
      .code-preview-container {
        margin: 0;
        overflow-x: auto;
        background: #0d1117; /* match github dark */
        padding: 16px;
        height: 100%;
        border-radius: var(--shape-3);
      }
      pre {
        margin: 0;
        font-family:
          ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, 'Liberation Mono', monospace;
        font-size: 13px;
        line-height: 1.45;
      }
      code {
        background: transparent;
        padding: 0;
      }
    `,
  ],
})
export class CodePreviewComponent implements OnChanges {
  content = input.required<string>();
  fileType = input.required<string>();

  highlightedCode = signal<SafeHtml>('');

  constructor(private sanitizer: DomSanitizer) {}

  ngOnChanges(): void {
    if (this.content()) {
      let lang = this.fileType().replace('.', '');
      if (lang === 'txt' || lang === 'log') {
        // Plain text
        this.highlightedCode.set(
          this.sanitizer.bypassSecurityTrustHtml(this.escapeHtml(this.content())),
        );
        return;
      }

      try {
        const validLang = hljs.getLanguage(lang) ? lang : 'plaintext';
        const highlighted = hljs.highlight(this.content(), { language: validLang }).value;
        this.highlightedCode.set(this.sanitizer.bypassSecurityTrustHtml(highlighted));
      } catch (err) {
        // Fallback
        this.highlightedCode.set(
          this.sanitizer.bypassSecurityTrustHtml(this.escapeHtml(this.content())),
        );
      }
    }
  }

  private escapeHtml(unsafe: string): string {
    return unsafe
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#039;');
  }
}
