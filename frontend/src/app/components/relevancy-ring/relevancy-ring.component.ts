import { Component, input, computed } from '@angular/core';

@Component({
  selector: 'app-relevancy-ring',
  standalone: true,
  template: `
    <div class="ring-container" [style.width.px]="size()" [style.height.px]="size()">
      <svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 36 36" class="circular-chart">
        <!-- Background ring -->
        <path class="circle-bg"
          d="M18 2.0845
            a 15.9155 15.9155 0 0 1 0 31.831
            a 15.9155 15.9155 0 0 1 0 -31.831"
        />
        <!-- Progress ring -->
        <path class="circle"
          [attr.stroke-dasharray]="dashArray()"
          [style.stroke]="ringColor()"
          d="M18 2.0845
            a 15.9155 15.9155 0 0 1 0 31.831
            a 15.9155 15.9155 0 0 1 0 -31.831"
        />
      </svg>
      <div class="score-text" [style.color]="ringColor()">
        {{ percentageValue() }}
      </div>
    </div>
  `,
  styles: [`
    .ring-container {
      position: relative;
      display: inline-flex;
      align-items: center;
      justify-content: center;
    }
    .circular-chart {
      display: block;
      margin: 0 auto;
      max-width: 100%;
      max-height: 100%;
    }
    .circle-bg {
      fill: none;
      stroke: var(--sh-color-border, #e2e8f0);
      stroke-width: 3.8;
      opacity: 0.3;
    }
    .circle {
      fill: none;
      stroke-width: 3.5;
      stroke-linecap: round;
      transition: stroke-dasharray 0.8s ease-out, stroke 0.3s ease;
    }
    .score-text {
      position: absolute;
      top: 50%;
      left: 50%;
      transform: translate(-50%, -50%);
      font-size: 11px;
      font-weight: 700;
      font-family: var(--sh-font-mono, monospace);
    }
  `]
})
export class RelevancyRingComponent {
  score = input.required<number>(); // Expected to be 0 to 1
  size = input<number>(32); // Default size 32px
  
  percentageValue = computed(() => {
    return Math.round(this.score() * 100);
  });

  dashArray = computed(() => {
    const percentage = Math.max(0, Math.min(100, this.percentageValue()));
    return `${percentage}, 100`;
  });

  ringColor = computed(() => {
    const p = this.percentageValue();
    // Smooth HSL gradient from Red (Hue 0) -> Yellow (Hue 60) -> Green (Hue 120)
    // Lightness 42% and Saturation 85% gives us that deeper 'level 8' rich tone
    const hue = Math.round(p * 1.2); 
    return `hsl(${hue}, 85%, 42%)`;
  });
}
