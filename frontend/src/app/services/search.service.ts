import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';

export interface SearchResult {
  id: string;
  content: string;
  score: number;
  rank: number;
  source: string;
  metadata: Record<string, any>;
}

export interface RrfSearchRequest {
  query: string;
  topKPerMethod?: number;
  finalTopK?: number;
  rrfK?: number;
  vectorWeight?: number;
  sparseWeight?: number;
  useWeightedFusion?: boolean;
}

export interface RrfSearchResponse {
  query: string;
  totalCount: number;
  results: SearchResult[];
}

@Injectable({
  providedIn: 'root'
})
export class SearchService {
  private http = inject(HttpClient);
  private endpoint = '/api/search/rrf';

  searchQuery(request: RrfSearchRequest): Observable<SearchResult[]> {
    return this.http.post<RrfSearchResponse>(this.endpoint, request).pipe(
      map(res => res.results)
    );
  }
}
