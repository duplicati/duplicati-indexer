import { Pipe, PipeTransform } from '@angular/core';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { parse } from 'marked';
import DOMPurify from 'dompurify';

@Pipe({
  name: 'markdown',
  standalone: true
})
export class MarkdownPipe implements PipeTransform {
  constructor(private sanitizer: DomSanitizer) {}

  transform(value: string | undefined | null): SafeHtml {
    if (!value) {
      return '';
    }

    try {
      // Parse the markdown string to HTML
      let html = '';
      if (typeof parse === 'function') {
        html = parse(value, { async: false }) as string;
      } else {
        // Fallback if marked failed to import correctly
        html = (value as any);
        console.error("marked.parse is not a function", { parse });
      }
      
      let sanitizedHtml = html;
      const purify: any = DOMPurify;
      
      if (purify && typeof purify.sanitize === 'function') {
        sanitizedHtml = purify.sanitize(html);
      } else if (purify && purify.default && typeof purify.default.sanitize === 'function') {
        sanitizedHtml = purify.default.sanitize(html);
      }
      
      // Bypass Angular's built-in sanitizer since we already sanitized it with DOMPurify
      // This allows classes and certain elements that Angular might strip
      return this.sanitizer.bypassSecurityTrustHtml(sanitizedHtml);
    } catch (e) {
      console.error("Markdown pipe error:", e);
      return this.sanitizer.bypassSecurityTrustHtml(value);
    }
  }
}
