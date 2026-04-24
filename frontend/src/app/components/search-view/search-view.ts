import { Component, signal, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import {
  ShipFormField, ShipButton, ShipIcon, ShipCard, 
  ShipSpinner, ShipChip, ShipRangeSlider
} from '@ship-ui/core';

import { SearchService, SearchResult } from '../../services/search.service';

@Component({
  selector: 'app-search-view',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ShipFormField, ShipButton, 
    ShipIcon, ShipCard, ShipSpinner, ShipChip, ShipRangeSlider
  ],
  styles: [`
    .search-container { padding: 24px; max-width: 1200px; margin: 0 auto; display: flex; flex-direction: column; gap: 24px; }
    .search-header { display: flex; flex-direction: column; gap: 16px; margin-bottom: 8px; }
    .search-bar { display: flex; gap: 12px; align-items: flex-start; width: 100%; }
    .search-bar sh-form-field { flex: 1; }
    .sliders { display: flex; gap: 24px; background: var(--sh-base-1l); padding: 16px; border-radius: 8px; }
    .slider-box { flex: 1; display: flex; flex-direction: column; gap: 4px; }
    .slider-box label { font-size: 13px; font-weight: 500; color: var(--sh-text-secondary); }
    .results-area { display: flex; flex-direction: column; gap: 16px; }
    .result-card { padding: 16px; display: flex; flex-direction: column; gap: 12px; }
    .result-header { display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid var(--sh-base-3); padding-bottom: 12px; }
    .result-meta { display: flex; gap: 12px; align-items: center; font-size: 13px; color: var(--sh-text-secondary); }
    .result-content { font-size: 14px; line-height: 1.5; color: var(--sh-text-primary); white-space: pre-wrap; font-family: monospace; padding: 8px; background: var(--sh-base-1); border-radius: 4px; }
  `],
  template: `
    <div class="search-container">
      <div class="search-header">
        <h2 style="margin: 0; font-size: 24px; font-weight: 600;">Raw Dataset Search Telemetry</h2>
        <p style="margin: 0; color: var(--sh-text-secondary); font-size: 14px;">Directly query the underlying data vector and full-text structures natively bypassing AI conversational generation.</p>
        
        <div class="search-bar">
          <sh-form-field>
            <input type="text" [(ngModel)]="query" (keydown.enter)="executeSearch()" placeholder="Enter raw evaluation string (e.g. Enron Broadband)..." [disabled]="isLoading()" />
          </sh-form-field>
          <button shButton class="primary raised" (click)="executeSearch()" [disabled]="!query() || isLoading()">
            <sh-icon>magnifying-glass</sh-icon>
            Evaluate
          </button>
        </div>

        <div class="sliders">
          <div class="slider-box">
            <label>Vector Weight (Semantic Concept)</label>
            <sh-range-slider color="primary" [alwaysShow]="true">
              <input type="range" min="0" max="1" step="0.1" [(ngModel)]="vectorWeight" [disabled]="isLoading()" />
            </sh-range-slider>
          </div>
          <div class="slider-box">
            <label>Sparse Weight (Keyword Exact)</label>
            <sh-range-slider color="accent" [alwaysShow]="true">
              <input type="range" min="0" max="1" step="0.1" [(ngModel)]="sparseWeight" [disabled]="isLoading()" />
            </sh-range-slider>
          </div>
        </div>
      </div>

      <div class="results-area">
        @if (isLoading()) {
          <div style="display: flex; gap: 12px; align-items: center; padding: 32px; justify-content: center; color: var(--sh-text-secondary);">
            <sh-spinner></sh-spinner> Executing native Reciprocal Rank Fusion hybrid pipeline...
          </div>
        } @else if (results().length > 0) {
          <h3 style="margin: 0; font-size: 16px;">Telemetry Pipeline Extracted {{ results().length }} Results:</h3>
          @for (res of results(); track res.id) {
            <sh-card class="result-card outline">
              <div class="result-header">
                <div class="result-meta">
                  <span style="font-weight: 600; color: var(--sh-text-primary);">Rank #{{ res.rank }}</span>
                  <sh-chip [color]="res.source === 'hybrid' ? 'success' : 'primary'" size="small" variant="flat">{{ res.source }}</sh-chip>
                  <span>Score: {{ res.score | number:'1.2-4' }}</span>
                </div>
                <div class="result-meta">
                  <sh-icon style="font-size: 14px;">file-text</sh-icon>
                  {{ res.metadata['Filename'] || 'Unknown File' }}
                </div>
              </div>
              <div class="result-content">{{ res.content }}</div>
            </sh-card>
          }
        } @else if (hasSearched()) {
          <div style="padding: 32px; text-align: center; color: var(--sh-text-secondary); background: var(--sh-base-1l); border-radius: 8px;">
            Dataset evaluation completed implicitly: No chunk bindings matched testing criteria bounds!
          </div>
        }
      </div>
    </div>
  `
})
export class SearchViewComponent {
  private searchService = inject(SearchService);

  query = signal('');
  vectorWeight = signal(0.7);
  sparseWeight = signal(0.3);
  isLoading = signal(false);
  hasSearched = signal(false);
  results = signal<SearchResult[]>([]);

  executeSearch() {
    if (!this.query().trim() || this.isLoading()) return;
    
    this.isLoading.set(true);
    this.hasSearched.set(true);
    this.results.set([]);

    this.searchService.searchQuery({
      query: this.query().trim(),
      vectorWeight: this.vectorWeight(),
      sparseWeight: this.sparseWeight(),
      useWeightedFusion: true,
      finalTopK: 10
    }).subscribe({
      next: (data) => {
        this.results.set(data);
        this.isLoading.set(false);
      },
      error: (err) => {
        console.error('Search failed:', err);
        this.isLoading.set(false);
      }
    });
  }
}
