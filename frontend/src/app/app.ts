import { CommonModule, Location } from '@angular/common';
import {
  AfterViewChecked,
  Component,
  ElementRef,
  inject,
  OnDestroy,
  OnInit,
  signal,
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
}

@Component({
  selector: 'app-root',
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
  ],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export default class App implements AfterViewChecked, OnDestroy, OnInit {
  queryService = inject(QueryService);
  dialog = inject(ShipDialogService);
  location = inject(Location);
  conversationContainer = viewChild<ElementRef>('conversationContainer');

  queryText = signal('What was the most common request from Ken Lay?');
  isLoading = signal(false);
  hasSubmitted = signal(false);
  sessionId = signal<string | null>(null);
  conversation = signal<ConversationMessage[]>([]);
  sessions = signal<ChatSession[]>([]);
  isNavOpen = signal(true);
  isDarkMode = signal(false);
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
    const urlParams = new URLSearchParams(window.location.search);
    const chatId = urlParams.get('chat');
    if (chatId) {
      this.loadSession(chatId);
    }
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

  loadSession(id: string): void {
    this.abortQuery();
    this.sessionId.set(id);
    this.hasSubmitted.set(true);
    this.isLoading.set(true);
    this.conversation.set([]); // Clear temporarily until loaded
    this.location.replaceState(`?chat=${id}`);

    this.queryService.getSessionHistory(id).subscribe({
      next: (details) => {
        const historyConv = details.history.map((h) => ({
          id: h.id,
          query: h.originalQuery,
          response: h.response,
          events: h.events,
          isLoading: false,
        }));
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
    };

    this.conversation.update((c) => [...c, newMessage]);
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

            if (event.eventType === 'final') {
              message.response = event.content;
              message.isLoading = false;
              if (event.eventContext) {
                this.sessionId.set(event.eventContext);
                this.location.replaceState(`?chat=${event.eventContext}`);
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
    this.hasSubmitted.set(false);
    this.sessionId.set(null);
    this.location.replaceState('');
  }
}
