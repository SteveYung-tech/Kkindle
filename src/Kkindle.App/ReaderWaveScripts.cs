using System.Globalization;

namespace Kkindle;

// Builds the injected overlay that renders the "水波流动" (Kindle-style swipe)
// page-turn animation inside the reader WebView. The captured page is sliced
// into vertical strips; each strip slides and fades away from right to left
// (forward turns) with a sine-modulated delay, slide and inner flow, so the
// old page visibly "flows" like a water wave while the real next page
// underneath is revealed progressively from the incoming side. A soft light
// band sweeps with the wave front, mimicking the shimmer of the new-gen
// Kindle e-ink wipe.
//
// The animation is driven entirely by CSS (@property-registered progress,
// sin() in transforms, per-strip delays and gradients), so it keeps running
// even though the reader runs with IsScriptEnabled=false; only the one-shot
// DOM injection is a script.
internal static class ReaderWaveScripts
{
    // Total host-side wait for one wave, including per-strip start delays.
    public const int TotalDurationMs = 560;

    private const int StripCount = 32;
    // Each strip takes this long to slide/fade once its turn starts.
    private const int StripDurationMs = 170;
    // The first strip starts immediately and the last one after SpanMs, so the
    // leading edge of the wave reaches the far side just before the end.
    private const int SpanMs = TotalDurationMs - StripDurationMs;
    // Sine modulation added on top of the monotonic right-to-left stagger, so
    // the reveal front is wavy instead of a straight curtain.
    private const double DelayWaveMs = 26;
    private const double WaveCount = 1.5;

    public static string CreateWaveOverlayScript(
        string dataUrl,
        double width,
        double height,
        bool forward)
    {
        var w = Format(width);
        var h = Format(height);
        var stripW = Format(Math.Max(1, width / StripCount));
        var dir = forward ? -1 : 1;
        // Gloss sweep start/end offsets (baked with the direction sign):
        // forward starts off-screen right and exits left; backward mirrors it.
        var glossStart = Format(-dir * width);
        var glossEnd = Format(dir * width * 0.42);
        return $$"""
            (() => {
              try {
                const root = document.documentElement;
                const W = {{w}}, H = {{h}}, N = {{StripCount}}, STRIP_W = {{stripW}};
                if (W < 2 || H < 2) return false;
                const DATA = "{{dataUrl}}";
                const DIR = {{dir}};
                const DUR = {{TotalDurationMs}};
                const STRIP_MS = {{StripDurationMs}};
                const SPAN = {{SpanMs}};
                const AMP = {{DelayWaveMs}};
                const WAVES = {{WaveCount}};

                let style = document.getElementById('kk-wave-style');
                if (!style) {
                  style = document.createElement('style');
                  style.id = 'kk-wave-style';
                  document.head.appendChild(style);
                }
                style.textContent = `
                  @property --kk-t { syntax: '<number>'; inherits: false; initial-value: 0; }
                  @property --kk-g { syntax: '<number>'; inherits: false; initial-value: 0; }
                  #kk-wave { position: fixed; left: 0; top: 0; width: 100%; height: 100%;
                              overflow: hidden; pointer-events: none; z-index: 2147483000;
                              background: transparent; }
                  #kk-wave .kk-strip { position: absolute; left: 0; top: 0; width: {{stripW}}px;
                                       height: 100%; opacity: 0; will-change: transform, opacity;
                                       background-repeat: no-repeat;
                                       background-position-y: 0;
                                       background-position-x: calc(var(--kk-bx) + var(--kk-flow) * var(--kk-t));
                                       transform: translateX(calc(var(--kk-x) + var(--kk-slide) * var(--kk-t)))
                                                  translateY(calc(var(--kk-ripple) * sin(calc(var(--kk-t) * 3.14159265))))
                                                  rotate(calc(var(--kk-tilt) * var(--kk-t) * 1deg));
                                       animation: kk-wash {{StripDurationMs}}ms ease-in-out both; }
                  @keyframes kk-wash {
                    0%   { --kk-t: 0; opacity: 1; filter: brightness(1); }
                    55%  { opacity: 0.96; filter: brightness(1.05); }
                    100% { --kk-t: 1; opacity: 0; filter: brightness(0.92); }
                  }
                  #kk-wave .kk-gloss { position: absolute; left: 0; top: 0; width: 42%;
                                       height: 100%; opacity: 0; will-change: transform, opacity;
                                       background: linear-gradient(90deg,
                                         transparent 0%, rgba(255,255,255,0.10) 28%,
                                         rgba(255,255,255,0.30) 50%, rgba(255,255,255,0.10) 72%,
                                         transparent 100%);
                                       transform: translateX(calc(var(--kk-gs) + (var(--kk-ge) - var(--kk-gs)) * var(--kk-g)));
                                       animation: kk-gloss {{TotalDurationMs}}ms ease-out both; }
                  @keyframes kk-gloss {
                    0%   { --kk-g: 0; opacity: 0.55; }
                    18%  { opacity: 0.9; }
                    72%  { opacity: 0.35; }
                    100% { --kk-g: 1; opacity: 0; }
                  }
                `;

                const container = document.createElement('div');
                container.id = 'kk-wave';
                const gloss = document.createElement('div');
                gloss.className = 'kk-gloss';
                gloss.style.setProperty('--kk-gs', {{glossStart}} + 'px');
                gloss.style.setProperty('--kk-ge', {{glossEnd}} + 'px');
                container.appendChild(gloss);

                for (let j = 0; j < N; j++) {
                  const phase = 2 * Math.PI * WAVES * (j / N);
                  const wave = Math.sin(phase);
                  const order = DIR < 0 ? (N - 1 - j) : j;
                  const delay = Math.max(0, (order / N) * SPAN + AMP * wave);
                  const strip = document.createElement('div');
                  strip.className = 'kk-strip';
                  strip.style.setProperty('--kk-x', (j * STRIP_W).toFixed(2) + 'px');
                  strip.style.setProperty('--kk-bx', (-j * STRIP_W).toFixed(2) + 'px');
                  strip.style.setProperty('--kk-slide', (DIR * (0.42 + 0.16 * wave) * STRIP_W).toFixed(2) + 'px');
                  strip.style.setProperty('--kk-flow', (DIR * (0.78 + 0.16 * wave) * STRIP_W).toFixed(2) + 'px');
                  strip.style.setProperty('--kk-ripple', (7 * wave).toFixed(2) + 'px');
                  strip.style.setProperty('--kk-tilt', (DIR * 4 * wave).toFixed(2));
                  strip.style.animationDelay = delay.toFixed(1) + 'ms';
                  strip.style.backgroundImage = 'url("' + DATA + '")';
                  strip.style.backgroundSize = W + 'px ' + H + 'px';
                  container.appendChild(strip);
                }

                root.appendChild(container);
                return true;
              } catch (_) {
                const old = document.getElementById('kk-wave');
                if (old) old.remove();
                const st = document.getElementById('kk-wave-style');
                if (st) st.remove();
                return false;
              }
            })();
            """;
    }

    public static string CreateWaveCleanupScript() =>
        """
        (() => {
          const el = document.getElementById('kk-wave');
          if (el) el.remove();
          const st = document.getElementById('kk-wave-style');
          if (st) st.remove();
          return true;
        })();
        """;

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
