import { CommonModule, Location } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import {
  AfterViewChecked,
  Component,
  ElementRef,
  inject,
  OnDestroy,
  OnInit,
  signal,
  computed,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ShipAlert,
  ShipButton,
  ShipCard,
  ShipDivider,
  ShipFormField,
  ShipIcon,
  ShipList,
  ShipSidenav,
  ShipSpinner,
  ShipDialogService,
  ShipChip
} from '@ship-ui/core';
import { ConfirmDialogComponent } from './components/confirm-dialog/confirm-dialog.component';
import { Subscription } from 'rxjs';
import LogoComponent from './components/logo/logo.component';
import { SearchViewComponent } from './components/search-view/search-view';
import { MarkdownPipe } from './markdown.pipe';
import {
  ChatSession,
  QueryService,
  RagQueryEvent,
  RagQueryRequest,
} from './services/query.service';

export interface ConversationMessage {
  id: string;
  query: string;
  response: string;
  isLoading: boolean;
  events?: RagQueryEvent[];
  timestamp?: Date;
  latestEvent?: RagQueryEvent;
}

@Component({
  selector: 'app-chat',
  imports: [
    CommonModule,
    FormsModule,
    ShipSidenav,
    ShipList,
    ShipButton,
    ShipIcon,
    ShipCard,
    ShipAlert,
    ShipSpinner,
    ShipFormField,
    ShipDivider,
    ShipChip,

    LogoComponent,
    SearchViewComponent,
    MarkdownPipe
  ],
  templateUrl: './chat.component.html',
  styleUrl: './chat.component.scss',
})
export default class ChatComponent implements AfterViewChecked, OnDestroy, OnInit {
  queryService = inject(QueryService);
  dialog = inject(ShipDialogService);
  route = inject(ActivatedRoute);
  router = inject(Router);
  location = inject(Location);
  conversationContainer = viewChild<ElementRef>('conversationContainer');

  viewMode = signal<'chat' | 'search'>('chat');
  queryText = signal('What was the most common request from Ken Lay?');
  isLoading = signal(false);
  hasSubmitted = signal(false);
  sessionId = signal<string | null>(null);
  conversation = signal<ConversationMessage[]>([]);
  sessions = signal<ChatSession[]>([]);
  isNavOpen = signal(true);
  isDarkMode = signal(false);
  relevantFiles = signal<{path: string, score: number}[]>([]);
  
  hoveredFiles = signal<string[] | null>(null);
  selectedFiles = signal<string[] | null>(null);
  filteredRelevantFiles = computed(() => {
     const allFiles = this.relevantFiles();
     const filter = this.selectedFiles();
     if (!filter) return allFiles;
     return allFiles.filter(f => filter.includes(f.path));
  });
  sidenavType = signal('simple');
  dbCount = signal(0);
  sparseCount = signal(0);
  metadataCount = signal(0);

  private querySubscription?: Subscription;
  private statsSubscription?: Subscription;
  private scrollPendingCount = 0;
  private scrollTimeout: ReturnType<typeof setTimeout> | null = null;
  private lastConversationLength = 0;
  private lastResponseText = '';

  toggleBodyClass(): void {
    this.isDarkMode.set(!this.isDarkMode());
    if (this.isDarkMode()) {
      document.documentElement.classList.add('dark');
      document.documentElement.classList.remove('light');
    } else {
      document.documentElement.classList.remove('dark');
      document.documentElement.classList.add('light');
    }
  }

  ngOnInit(): void {
    this.fetchSessions();
    this.subscribeDbStats();
    this.route.paramMap.subscribe(params => {
      const chatId = params.get('id');
      if (chatId && this.sessionId() !== chatId) {
        this.loadSession(chatId);
      } else if (!chatId) {
        // If we navigate to root, clear the chat
        if (this.sessionId()) {
          this.abortQuery();
          this.queryText.set('');
          this.conversation.set([]);
          this.relevantFiles.set([]);
          this.hasSubmitted.set(false);
          this.sessionId.set(null);
        }
      }
    });
  }

  subscribeDbStats(): void {
    this.statsSubscription = this.queryService.streamDatabaseStats().subscribe({
      next: (stats) => {
        this.dbCount.set(stats.documentCount);
        this.sparseCount.set(stats.sparseCount);
        this.metadataCount.set(stats.metadataCount);
      },
      error: (err) => console.error('Failed to stream DB stats organically', err)
    });
  }

  ngAfterViewChecked(): void {
    if (this.scrollPendingCount > 0 && this.conversationContainer()) {
      this.scheduleScrollToBottom();
    }

    const currentLen = this.conversation().length;
    if (currentLen !== this.lastConversationLength) {
      this.lastConversationLength = currentLen;
      this.scheduleScrollToBottom();
    }

    const currentResponseText = this.getLastResponseText();
    if (currentResponseText !== this.lastResponseText) {
      this.lastResponseText = currentResponseText;
      this.scheduleScrollToBottom();
    }
  }

  ngOnDestroy(): void {
    if (this.scrollTimeout) {
      clearTimeout(this.scrollTimeout);
    }
    this.statsSubscription?.unsubscribe();
  }

  private getLastResponseText(): string {
    const conv = this.conversation();
    if (conv.length === 0) return '';
    const lastMessage = conv[conv.length - 1];
    return lastMessage?.response || '';
  }

  private scheduleScrollToBottom(): void {
    if (this.scrollTimeout) {
      clearTimeout(this.scrollTimeout);
    }
    this.scrollTimeout = setTimeout(() => {
      this.scrollToBottom();
    }, 100);
  }

  private scrollToBottom(): void {
    const containerRef = this.conversationContainer();
    if (!containerRef) return;

    const container = containerRef.nativeElement;
    const scrollHeight = container.scrollHeight;

    container.scrollTo({
      top: scrollHeight,
      behavior: 'smooth',
    });

    if (this.scrollPendingCount > 0) {
      this.scrollPendingCount--;
      if (this.scrollPendingCount > 0) {
        setTimeout(() => this.scrollToBottom(), 150);
      }
    }
  }

  private requestScroll(): void {
    this.scrollPendingCount++;
    this.scheduleScrollToBottom();
  }

  fetchSessions(): void {
    this.queryService.getSessions().subscribe({
      next: (data) => this.sessions.set(data),
      error: (err) => console.error('Failed to load sessions', err),
    });
  }

  openFilePreview(path: string): void {
    console.log(`[Frontend] Triggered openFilePreview for: ${path}`);
    
    // Open dialog immediately with loading state
    import('./components/file-preview.component').then(m => {
      const dialogData = { 
        path: path, 
        content: signal(''), 
        loading: signal(true) 
      };
      
      this.dialog.open(m.FilePreviewComponent, {
        data: dialogData
      });

      this.queryService.getFileContent(path).subscribe({
        next: (response) => {
          console.log(`[Frontend] Received content for: ${path}`, response);
          dialogData.content.set(response.content);
          dialogData.loading.set(false);
        },
        error: (err) => {
          console.error('[Frontend] Failed to preview file via API:', err);
          dialogData.content.set('Failed to load file content.\n\nError: ' + err.message);
          dialogData.loading.set(false);
        }
      });
    }).catch(err => console.error('Failed to load modal component', err));
  }

  loadSession(id: string): void {
    this.abortQuery();
    this.sessionId.set(id);
    this.hasSubmitted.set(true);
    this.isLoading.set(true);
    this.conversation.set([]); // Clear temporarily until loaded
    this.relevantFiles.set([]); // Reset on changing session
    this.router.navigate(['/session', id]);

    this.queryService.getSessionHistory(id).subscribe({
      next: (details) => {
        const fileMap = new Map<string, number>();
        const historyConv = details.history.map((h) => {
          let lastThoughtEvent: RagQueryEvent | null = null;
          // Extract any files found historically
          h.events?.forEach(event => {
            if (event.eventType === 'thought') {
              lastThoughtEvent = event;
            } else if (event.eventType === 'action' && lastThoughtEvent) {
              lastThoughtEvent.actionContent = event.content;
            }
            
            if (event.eventType === 'relevant_files') {
              try {
                // Handle backwards compatibility (string[]) or new schema ({path, score}[])
                const parsed = JSON.parse(event.content);
                const localPaths: string[] = [];
                if (Array.isArray(parsed)) {
                  parsed.forEach(item => {
                    if (typeof item === 'string') {
                      if (!fileMap.has(item)) fileMap.set(item, 0);
                      localPaths.push(item);
                    } else if (item.path !== undefined) {
                      const currentScore = fileMap.get(item.path) || 0;
                      fileMap.set(item.path, currentScore + item.score);
                      localPaths.push(item.path);
                    }
                  });
                }
                if (lastThoughtEvent && localPaths.length > 0) {
                  lastThoughtEvent.associatedFiles = localPaths;
                }
              } catch(e) {}
            }
          });

          return {
            id: h.id,
            query: h.originalQuery,
            response: h.response,
            events: h.events?.filter(e => e.eventType === 'thought'),
            isLoading: false,
            timestamp: new Date(h.queryTimestamp)
          };
        });
        const resolvedFiles = Array.from(fileMap.entries()).map(([path, score]) => ({ path, score }));
        resolvedFiles.sort((a, b) => b.score - a.score); // Highest relevance first
        
        this.relevantFiles.set(resolvedFiles);
        this.conversation.set(historyConv);
        this.isLoading.set(false);
        this.requestScroll();
      },
      error: (err) => {
        console.error('Failed to load session history', err);
        this.isLoading.set(false);
      },
    });
  }

  abortQuery(): void {
    if (this.querySubscription) {
      this.querySubscription.unsubscribe();
      this.querySubscription = undefined;

      this.conversation.update((c) => {
        if (c.length > 0) {
          const last = c[c.length - 1];
          if (last.isLoading) {
            last.isLoading = false;
            if (!last.response) {
              last.response = 'Query aborted by user.';
            }
          }
        }
        return [...c];
      });
      this.isLoading.set(false);
    }
  }

  onSubmit(): void {
    const query = this.queryText().trim();
    if (!query || this.isLoading()) {
      return;
    }

    this.isLoading.set(true);
    this.hasSubmitted.set(true);

    const messageId = Date.now().toString();
    const newMessage: ConversationMessage = {
      id: messageId,
      query: query,
      response: '',
      isLoading: true,
      timestamp: new Date()
    };

    this.conversation.update((c) => [...c, newMessage]);
    if (this.conversation().length === 1) {
      this.relevantFiles.set([]); // Clear on purely new conversation
    }
    this.queryText.set('');
    this.requestScroll();

    const request: RagQueryRequest = {
      query: query,
      sessionId: this.sessionId(),
    };

    if (this.querySubscription) {
      this.querySubscription.unsubscribe();
    }

    this.querySubscription = this.queryService.streamQuery(request).subscribe({
      next: (event: RagQueryEvent) => {
        this.conversation.update((c) => {
          const index = c.findIndex((m) => m.id === messageId);
          if (index !== -1) {
            const message = { ...c[index] };
            message.events = message.events ? [...message.events] : [];

            if (event.eventType === 'session_init') {
              if (!this.sessionId()) {
                this.sessionId.set(event.content);
                this.location.go(`/session/${event.content}`);
                this.fetchSessions();
              }
            } else if (event.eventType === 'answer_chunk') {
              message.isLoading = false; // Stop the "Thinking..." spinner
              message.response = (message.response || '') + event.content;
            } else if (event.eventType === 'final') {
              message.response = event.content;
              message.isLoading = false;
              if (event.eventContext && !this.sessionId()) {
                this.sessionId.set(event.eventContext);
                this.location.go(`/session/${event.eventContext}`);
              }
            } else if (event.eventType === 'info') {
              if (!event.content.includes('Evaluating chunk contexts and consulting AI')) {
                message.latestEvent = event;
              }
            } else if (event.eventType === 'relevant_files') {
              try {
                // Parse either old schema (string[]) or new schema ({path, score}[])
                const parsed = JSON.parse(event.content);
                const localPaths: string[] = [];
                this.relevantFiles.update(current => {
                  const map = new Map<string, number>();
                  current.forEach(c => map.set(c.path, c.score));
                  
                  if (Array.isArray(parsed)) {
                    parsed.forEach(item => {
                      if (typeof item === 'string') {
                        if (!map.has(item)) map.set(item, 0);
                        localPaths.push(item);
                      } else if (item.path !== undefined) {
                        const existingScore = map.get(item.path) || 0;
                        map.set(item.path, existingScore + item.score);
                        localPaths.push(item.path);
                      }
                    });
                  }
                  
                  const resolvedFiles = Array.from(map.entries()).map(([path, score]) => ({ path, score }));
                  resolvedFiles.sort((a, b) => b.score - a.score);
                  return resolvedFiles;
                });
                
                if (localPaths.length > 0 && message.events && message.events.length > 0) {
                   for (let i = message.events.length - 1; i >= 0; i--) {
                       if (message.events[i].eventType === 'thought') {
                           message.events[i].associatedFiles = localPaths;
                           break;
                       }
                   }
                }
              } catch(e) {
                console.error('Failed to parse relevant_files', e);
              }
            } else if (event.eventType === 'action') {
              if (message.events.length > 0) {
                 for (let i = message.events.length - 1; i >= 0; i--) {
                    if (message.events[i].eventType === 'thought') {
                       message.events[i].actionContent = event.content;
                       break;
                    }
                 }
              }
            } else {
              message.events.push(event);
            }
            c[index] = message;
          }
          return [...c];
        });
        if (event.eventType === 'final') {
          this.isLoading.set(false);
          this.fetchSessions(); // Refresh history titles
        }
        this.requestScroll();
      },
      error: (error) => {
        console.error('Query failed:', error);
        this.conversation.update((c) => {
          const message = c.find((m) => m.id === messageId);
          if (message) {
            message.response = 'Sorry, there was an error processing your query. Please try again.';
            message.isLoading = false;
          }
          return [...c];
        });
        this.isLoading.set(false);
        this.requestScroll();
      },
    });
  }

  onKeyDown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.dialog.open(ConfirmDialogComponent, {
        data: {
          title: 'Clear conversation?',
          message: 'Are you sure you want to start a new conversation?',
          confirmText: 'Clear'
        },
        closed: (result: boolean) => {
          if (result) {
            this.clearQuery();
          }
        }
      });
      return;
    }

    if (event.key === 'Enter' && !event.shiftKey) {
      event.preventDefault();
      this.onSubmit();
    }
  }

  clearQuery(): void {
    this.abortQuery();
    this.queryText.set('');
    this.conversation.set([]);
    this.relevantFiles.set([]);
    this.hasSubmitted.set(false);
    this.sessionId.set(null);
    this.router.navigate(['/']);
  }

  revertMessage(messageId: string): void {
    this.conversation.update(c => {
      const idx = c.findIndex(m => m.id === messageId);
      if (idx !== -1) {
        this.abortQuery();
        const msg = c[idx];
        this.queryText.set(msg.query);
        const newConversation = c.slice(0, idx); // remove this message and everything after it
        
        // Recalculate relevant files from remaining conversation
        const fileMap = new Map<string, number>();
        newConversation.forEach(msg => {
          msg.events?.forEach(event => {
            if (event.eventType === 'relevant_files') {
              try {
                const parsed = JSON.parse(event.content);
                if (Array.isArray(parsed)) {
                  parsed.forEach(item => {
                    if (typeof item === 'string') {
                      if (!fileMap.has(item)) fileMap.set(item, 0);
                    } else if (item.path !== undefined) {
                      const currentScore = fileMap.get(item.path) || 0;
                      fileMap.set(item.path, currentScore + item.score);
                    }
                  });
                }
              } catch(e) {}
            }
          });
        });
        const resolvedFiles = Array.from(fileMap.entries()).map(([path, score]) => ({ path, score }));
        resolvedFiles.sort((a, b) => b.score - a.score);
        this.relevantFiles.set(resolvedFiles);

        return newConversation;
      }
      return c;
    });
    const sid = this.sessionId();
    if (sid) {
      this.queryService.revertSession(sid, messageId).subscribe({
        next: () => console.log(`[Frontend] Successfully reverted session ${sid} on backend to message ${messageId}`),
        error: (err) => console.error(`[Frontend] Failed to revert session ${sid} on backend:`, err)
      });
    }

    if (this.conversation().length === 0) {
      this.hasSubmitted.set(false);
    }
  }

  hoverEvent(event: RagQueryEvent | null) {
      if (event && event.associatedFiles && event.associatedFiles.length > 0) {
         this.hoveredFiles.set(event.associatedFiles);
      } else {
         this.hoveredFiles.set(null);
      }
  }

  clickEvent(event: RagQueryEvent) {
      if (!event.associatedFiles || event.associatedFiles.length === 0) return;
      
      const current = this.selectedFiles();
      // toggle off if same
      if (current && current.join(',') === event.associatedFiles.join(',')) {
         this.selectedFiles.set(null);
      } else {
         this.selectedFiles.set(event.associatedFiles);
      }
  }

  clearFileFilter() {
     this.selectedFiles.set(null);
  }
}
