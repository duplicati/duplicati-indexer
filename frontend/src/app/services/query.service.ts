import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { timeout } from 'rxjs/operators';

export interface RagQueryRequest {
  query: string;
  topK?: number;
  sessionId?: string | null;
}

export interface RagQueryResponse {
  sessionId: string;
  answer: string;
  isNewSession: boolean;
}

export interface RagQueryEvent {
  eventType: string; // 'thought', 'action', 'final', etc.
  content: string;
  eventContext?: string; // e.g. sessionId for final
}

export interface ChatSession {
  id: string;
  title: string;
  createdAt: string;
  lastActivityAt: string;
}

export interface SessionHistoryItem {
  id: string;
  originalQuery: string;
  condensedQuery: string;
  response: string;
  queryTimestamp: string;
  responseTimestamp: string;
  events?: RagQueryEvent[];
}

export interface SessionDetailsResponse {
  id: string;
  title: string;
  createdAt: string;
  lastActivityAt: string;
  history: SessionHistoryItem[];
}

@Injectable({
  providedIn: 'root'
})
export class QueryService {
  private http = inject(HttpClient);
  private readonly apiUrl = '/api/rag/query';
  // 5 minute timeout to accommodate slow local LLM models
  private readonly requestTimeoutMs = 300000;

  query(request: RagQueryRequest): Observable<RagQueryResponse> {
    return this.http.post<RagQueryResponse>(this.apiUrl, request).pipe(
      timeout(this.requestTimeoutMs)
    );
  }

  getSessions(): Observable<ChatSession[]> {
    return this.http.get<ChatSession[]>('/api/rag/sessions');
  }

  getSessionHistory(sessionId: string): Observable<SessionDetailsResponse> {
    return this.http.get<SessionDetailsResponse>(`/api/rag/sessions/${sessionId}`);
  }

  streamDatabaseStats(): Observable<{ documentCount: number, sparseCount: number, metadataCount: number }> {
    return new Observable(observer => {
      const eventSource = new EventSource('/api/stats/stream');
      
      eventSource.onmessage = (event) => {
        try {
          const stats = JSON.parse(event.data);
          observer.next(stats);
        } catch (e) {
          console.error('Failed to parse database stats stream chunk:', e);
        }
      };

      eventSource.onerror = (err) => {
        // SSE natively auto-reconnects, just log warnings if connection hiccups
        console.warn('Realtime database stats stream warning:', err);
      };

      return () => eventSource.close();
    });
  }

  streamQuery(request: RagQueryRequest): Observable<RagQueryEvent> {
    return new Observable<RagQueryEvent>(observer => {
      const abortController = new AbortController();

      fetch(this.apiUrl + '/stream', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'Accept': 'text/event-stream'
        },
        body: JSON.stringify(request),
        signal: abortController.signal
      }).then(async response => {
        if (!response.body) {
          throw new Error('ReadableStream not yet supported in this browser.');
        }
        const reader = response.body.getReader();
        const decoder = new TextDecoder('utf-8');
        let buffer = '';

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });
          const lines = buffer.split('\n');
          buffer = lines.pop() || ''; // Keep the last incomplete line in the buffer

          for (const line of lines) {
            if (line.startsWith('data:')) {
              const dataStr = line.replace('data:', '').trim();
              if (dataStr) {
                console.log('Received raw streaming chunk:', dataStr);
                try {
                  const eventData = JSON.parse(dataStr) as RagQueryEvent;
                  console.log('Parsed stream event:', eventData);
                  observer.next(eventData);
                  if (eventData.eventType === 'final') {
                    observer.complete();
                    return;
                  }
                } catch (e) {
                  console.error('Failed to parse streaming JSON:', e, dataStr);
                }
              }
            }
          }
        }
        observer.complete();
      }).catch(err => {
        if (err.name !== 'AbortError') {
          observer.error(err);
        }
      });

      return () => abortController.abort();
    });
  }
}
