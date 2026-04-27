import { Component, input, output, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ShipButton, ShipIcon, ShipSpinner } from '@ship-ui/core';
import { MarkdownPreviewComponent } from './markdown-preview.component';
import { CodePreviewComponent } from './code-preview.component';
import { WritableSignal, isSignal } from '@angular/core';

export type FilePreviewData = {
  path: string;
  content: WritableSignal<string> | string;
  loading?: WritableSignal<boolean> | boolean;
};

@Component({
  selector: 'app-file-preview',
  standalone: true,
  imports: [CommonModule, ShipButton, ShipIcon, ShipSpinner, MarkdownPreviewComponent, CodePreviewComponent],
  template: `
    <header header class="modal-header">
      <div class="file-info">
        <sh-icon>{{ getFileIcon() }}</sh-icon>
        <span class="filename">{{ filename() }}</span>
        <span class="filetype">{{ fileType() }}</span>
      </div>
      <button shButton class="icon small ghost" (click)="close()">
        <sh-icon>x-square</sh-icon>
      </button>
    </header>
    
    <div content class="modal-body">
      @if (isLoading()) {
        <div class="loading-state">
          <sh-spinner></sh-spinner> Loading preview...
        </div>
      } @else {
        @if (isMarkdown()) {
          @defer (on immediate) {
            <app-markdown-preview [content]="resolvedContent()"></app-markdown-preview>
          } @placeholder {
            <div class="loading-state">Loading markdown parser...</div>
          }
        } @else {
          @defer (on immediate) {
            <app-code-preview [content]="resolvedContent()" [fileType]="fileType()"></app-code-preview>
          } @placeholder {
            <div class="loading-state">Loading code highlighter...</div>
          }
        }
      }
    </div>
  `,
  styles: [`
    .modal-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      width: 100%;
      gap: 24px;
    }
    .file-info {
      display: flex;
      align-items: center;
      gap: 12px;
      flex: 1;
      min-width: 0;
    }
    .filename {
      font-weight: 600;
      color: var(--sh-text-primary);
      font-size: 14px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
    .filetype {
      font-size: 11px;
      color: var(--sh-text-secondary);
      background: var(--sh-base-3);
      padding: 2px 6px;
      border-radius: 4px;
      text-transform: uppercase;
      flex-shrink: 0;
    }
    .modal-body {
      background: var(--sh-surface-ground);
      min-height: 200px;
    }
    .loading-state {
      padding: 64px 32px;
      text-align: center;
      color: var(--sh-text-secondary);
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 16px;
    }
  `]
})
export class FilePreviewComponent {
  data = input.required<FilePreviewData>();
  closed = output<void>();

  filename = computed(() => {
    const path = this.data().path;
    return path ? path.split('/').pop() || 'Unknown' : 'Unknown';
  });

  fileType = computed(() => {
    const fn = this.filename();
    const parts = fn.split('.');
    return parts.length > 1 ? `.${parts.pop()}` : '';
  });

  isLoading = computed(() => {
    const loading = this.data().loading;
    if (loading === undefined) return false;
    return isSignal(loading) ? loading() : loading;
  });

  resolvedContent = computed(() => {
    const content = this.data().content;
    return isSignal(content) ? content() : content;
  });

  isMarkdown(): boolean {
    return this.fileType().toLowerCase() === '.md';
  }

  getFileIcon(): string {
    const ext = this.fileType().toLowerCase().replace('.', '');
    if (ext === 'md') return 'file-code';
    if (ext === 'json') return 'brackets-curly';
    if (ext === 'cs' || ext === 'ts' || ext === 'js' || ext === 'sh' || ext === 'py') return 'code';
    if (ext === 'txt' || ext === 'log') return 'file-text';
    return 'file';
  }

  close() {
    this.closed.emit();
  }
}
