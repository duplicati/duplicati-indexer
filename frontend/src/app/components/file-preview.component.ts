import { Component, input, output, WritableSignal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ShipAlert, ShipButton, ShipIcon, ShipProgressBar } from '@ship-ui/core';

export type FilePreviewData = {
  path: string;
  content: WritableSignal<string>;
  loading: WritableSignal<boolean>;
};

@Component({
  selector: 'app-file-preview',
  standalone: true,
  imports: [CommonModule, ShipAlert, ShipButton, ShipIcon, ShipProgressBar],
  template: `
    <div class="preview-container" style="display: flex; flex-direction: column; gap: 16px; padding: 24px; max-width: 800px; width: 90vw; max-height: 85vh;">
      <div class="header" style="display: flex; justify-content: space-between; align-items: flex-start; gap: 16px;">
        <div style="flex: 1; min-width: 0;">
          <h2 style="margin: 0; font-size: 1.25rem; word-break: break-all;">
            <sh-icon size="small" style="margin-right: 8px;">file</sh-icon>
            {{ data()?.path?.split('/')?.pop()?.split('\\\\')?.pop() || data()?.path }}
          </h2>
          <p style="margin: 4px 0 0 0; font-size: 0.875rem; opacity: 0.7; word-break: break-all;">{{ data()?.path }}</p>
        </div>
        <button shButton variant="flat" size="small" (click)="close()">Close</button>
      </div>

      <sh-alert variant="primary" style="flex-shrink: 0;">
        <span style="font-weight: 500;">Note:</span> This is a textual preview reconstructed from indexed database vector chunks, not the original raw file.
      </sh-alert>

      <div class="content-wrapper" style="flex: 1; overflow-y: auto; background: var(--sh-surface-ground, #111113); border: 1px solid var(--sh-border-color, #2b2b2b); border-radius: 8px; padding: 16px; position: relative; overflow-x: hidden;">
        @if (data()?.loading?.()) {
          <sh-progress-bar class="indeterminate" color="primary" style="position: absolute; top: 0; left: 0; right: 0; width: 100%;"></sh-progress-bar>
        }
        <pre style="margin: 0; font-family: monospace; font-size: 0.875rem; white-space: pre-wrap; word-break: break-word; color: white; opacity: {{ data()?.loading?.() ? 0.5 : 1 }};">{{ data()?.content?.() }}</pre>
      </div>
    </div>
  `
})
export class FilePreviewComponent {
  data = input<FilePreviewData>();
  closed = output<void>();

  close() {
    this.closed.emit();
  }
}
