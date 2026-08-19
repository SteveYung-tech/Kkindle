using System.Globalization;
using System.Text;

namespace Kkindle;

// Builds native View Transition animations for in-document turns and captured
// page overlays for chapter transitions. Neither path transforms the live EPUB
// body, so animation cannot alter the multicolumn scroll extent.
internal static class ReaderWaveScripts
{
    public const int TotalDurationMs = 560;

    private const int WavePointCount = 13;
    private const int WaveBandCount = 18;

    public static string CreateWaveOverlayScript(
        string dataUrl,
        double width,
        double height,
        bool forward,
        int totalDurationMs = TotalDurationMs,
        bool startPaused = false)
    {
        var totalDuration = Math.Clamp(totalDurationMs, 240, 2000);
        var w = Format(width);
        var h = Format(height);
        var bandHeight = Format(Math.Max(2, height / WaveBandCount + 1));
        var old0 = CreateWaveClipPolygon(0, forward, showNewPage: false, 0);
        var old1 = CreateWaveClipPolygon(.18, forward, showNewPage: false, .8);
        var old2 = CreateWaveClipPolygon(.45, forward, showNewPage: false, 1.7);
        var old3 = CreateWaveClipPolygon(.72, forward, showNewPage: false, 2.5);
        var old4 = CreateWaveClipPolygon(.92, forward, showNewPage: false, 3.3);
        var old5 = CreateWaveClipPolygon(1, forward, showNewPage: false, 0);
        var bandStart = forward ? width - 44 : -52;
        var bandEnd = forward ? -52 : width - 44;
        return $$"""
            (() => {
              try {
                const root = document.documentElement;
                const W = {{w}}, H = {{h}};
                if (W < 2 || H < 2) return false;
                const DATA = "{{dataUrl}}";
                document.getElementById('kk-wave')?.remove();
                document.getElementById('kk-wave-style')?.remove();

                const style = document.createElement('style');
                style.id = 'kk-wave-style';
                style.textContent = `
                  #kk-wave { position: fixed; inset: 0; width: 100%; height: 100%;
                              overflow: hidden; pointer-events: none; z-index: 2147483000;
                              background: transparent; }
                  #kk-wave-image { position: absolute; inset: 0;
                                   width: ${W}px !important; height: ${H}px !important;
                                   max-width: none !important; max-height: none !important;
                                   margin: 0 !important; padding: 0 !important;
                                   will-change: clip-path, filter;
                                   animation: kk-kindle-refresh {{totalDuration}}ms linear both;
                                   animation-play-state: {{(startPaused ? "paused" : "running")}}; }
                  @keyframes kk-kindle-refresh {
                    0%   { clip-path: {{old0}}; filter: brightness(1) contrast(1); }
                    18%  { clip-path: {{old1}}; filter: grayscale(.65) brightness(.91) contrast(1.16); }
                    45%  { clip-path: {{old2}}; filter: grayscale(1) brightness(.84) contrast(1.28); }
                    72%  { clip-path: {{old3}}; filter: grayscale(.85) brightness(.94) contrast(1.12); }
                    92%  { clip-path: {{old4}}; filter: grayscale(.35) brightness(1.02) contrast(1.04); }
                    100% { clip-path: {{old5}}; filter: none; }
                  }
                  #kk-wave .kk-refresh-band { position: absolute; left: 0;
                                              top: var(--kk-y); width: 96px;
                                              height: {{bandHeight}}px; opacity: 0;
                                              will-change: transform, opacity;
                                              background: linear-gradient(90deg,
                                                transparent 0%, rgba(255,255,255,.72) 24%,
                                                rgba(80,80,80,.32) 49%, rgba(255,255,255,.82) 70%,
                                                transparent 100%);
                                              mix-blend-mode: multiply;
                                              transform: translate3d(calc(var(--kk-start) + var(--kk-offset)),0,0);
                                              animation: kk-refresh-front {{totalDuration}}ms linear both;
                                              animation-play-state: {{(startPaused ? "paused" : "running")}}; }
                  @keyframes kk-refresh-front {
                    0%   { opacity: 0; transform: translate3d(calc(var(--kk-start) + var(--kk-offset)),0,0); }
                    8%   { opacity: .92; }
                    55%  { opacity: .78; }
                    92%  { opacity: .9; }
                    100% { opacity: 0; transform: translate3d(calc(var(--kk-end) + var(--kk-offset)),0,0); }
                  }
                `;
                document.head.appendChild(style);

                const container = document.createElement('div');
                container.id = 'kk-wave';
                const canvas = document.createElement('canvas');
                canvas.id = 'kk-wave-image';
                canvas.dataset.kkReady = 'false';
                container.appendChild(canvas);

                for (let j = 0; j < {{WaveBandCount}}; j++) {
                  const band = document.createElement('div');
                  band.className = 'kk-refresh-band';
                  const phase = 2 * Math.PI * 1.65 * (j / {{WaveBandCount}});
                  band.style.setProperty('--kk-y', (j * H / {{WaveBandCount}}).toFixed(2) + 'px');
                  band.style.setProperty('--kk-offset', (Math.sin(phase) * 30).toFixed(2) + 'px');
                  band.style.setProperty('--kk-start', '{{Format(bandStart)}}px');
                  band.style.setProperty('--kk-end', '{{Format(bandEnd)}}px');
                  container.appendChild(band);
                }

                root.appendChild(container);
                window.__kkindleStartWaveOverlay = () => {
                  canvas.dataset.kkStartRequested = 'true';
                  if (canvas.dataset.kkReady !== 'true') return true;
                  container.querySelectorAll('#kk-wave-image, .kk-refresh-band').forEach(node => {
                    node.style.animationPlayState = 'running';
                  });
                  return true;
                };
                const encoded = DATA.slice(DATA.indexOf(',') + 1);
                const raw = atob(encoded);
                const bytes = new Uint8Array(raw.length);
                for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
                createImageBitmap(new Blob([bytes], { type: 'image/png' })).then(bitmap => {
                  canvas.width = bitmap.width;
                  canvas.height = bitmap.height;
                  canvas.getContext('2d', { alpha: false }).drawImage(bitmap, 0, 0);
                  bitmap.close();
                  canvas.dataset.kkReady = 'true';
                  if (canvas.dataset.kkStartRequested === 'true') {
                    window.__kkindleStartWaveOverlay?.();
                  }
                }).catch(() => {
                  canvas.dataset.kkReady = 'error';
                });
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

    public static string CreateWaveStartScript() =>
        """
        (() => {
          const wave = document.getElementById('kk-wave');
          if (!wave) return false;
          return window.__kkindleStartWaveOverlay?.() === true;
        })();
        """;

    public static string CreateSlideViewTransitionStartScript(
        bool forward,
        int durationMs) =>
        CreateViewTransitionStartScript(forward, durationMs, wave: false);

    public static string CreateWaveViewTransitionStartScript(
        bool forward,
        int durationMs) =>
        CreateViewTransitionStartScript(forward, durationMs, wave: true);

    public static string ViewTransitionReadyScript =>
        "Boolean(window.__kkindleViewTransitionReady)";

    public static string ViewTransitionReleaseScript =>
        "window.__kkindleViewTransitionRelease?.() === true";

    public static string ViewTransitionCleanupScript =>
        """
        (() => {
          try { window.__kkindleViewTransitionRelease?.(); } catch (_) {}
          try { window.__kkindleViewTransition?.skipTransition?.(); } catch (_) {}
          document.getElementById('kk-view-transition-style')?.remove();
          delete window.__kkindleViewTransition;
          delete window.__kkindleViewTransitionReady;
          delete window.__kkindleViewTransitionRelease;
          return true;
        })();
        """;

    public static string CreateWaveCleanupScript() =>
        """
        (() => {
          const el = document.getElementById('kk-wave');
          if (el) el.remove();
          const st = document.getElementById('kk-wave-style');
          if (st) st.remove();
          delete window.__kkindleStartWaveOverlay;
          return true;
        })();
        """;

    // The slide transition uses the same captured-page model as the wave: a
    // fixed, clipped overlay moves while the live multicolumn document changes
    // underneath. Transforming body/documentElement changes Chromium's scroll
    // extent and can clamp a fractional last-page position before it is shown.
    public static string CreateSlideOverlayScript(
        string dataUrl,
        double width,
        double height,
        bool forward,
        int durationMs,
        bool startPaused = false)
    {
        var duration = Math.Clamp(durationMs, 120, 1200);
        var w = Format(width);
        var h = Format(height);
        var clipEnd = forward ? "inset(0 100% 0 0)" : "inset(0 0 0 100%)";
        var edgeStart = forward ? width - 28 : -56;
        var edgeEnd = forward ? -56 : width - 28;
        return $$"""
            (() => {
              try {
                const root = document.documentElement;
                const W = {{w}}, H = {{h}};
                if (!root || W < 2 || H < 2) return false;
                document.getElementById('kk-slide')?.remove();
                document.getElementById('kk-slide-style')?.remove();

                const style = document.createElement('style');
                style.id = 'kk-slide-style';
                style.textContent = `
                  #kk-slide { position: fixed; inset: 0; width: 100%; height: 100%;
                              overflow: hidden; pointer-events: none; z-index: 2147483000; }
                  #kk-slide-image { position: absolute; left: 0; top: 0;
                                    width: ${W}px !important; height: ${H}px !important;
                                    max-width: none !important; max-height: none !important;
                                    margin: 0 !important; padding: 0 !important;
                                    opacity: 1;
                                    will-change: clip-path;
                                    clip-path: inset(0);
                                    animation: kk-slide-wipe {{duration}}ms cubic-bezier(.35,0,.18,1) both;
                                    animation-play-state: {{(startPaused ? "paused" : "running")}}; }
                  #kk-slide-edge { position: absolute; left: 0; top: 0;
                                   width: 72px; height: 100%; opacity: 0;
                                   background: linear-gradient(90deg,
                                     transparent, rgba(0,0,0,.15), rgba(255,255,255,.48), transparent);
                                   will-change: transform, opacity;
                                   animation: kk-slide-edge {{duration}}ms cubic-bezier(.35,0,.18,1) both;
                                   animation-play-state: {{(startPaused ? "paused" : "running")}}; }
                  @keyframes kk-slide-wipe {
                    0% { clip-path: inset(0); }
                    100% { clip-path: {{clipEnd}}; }
                  }
                  @keyframes kk-slide-edge {
                    0% { opacity: 0; transform: translate3d({{Format(edgeStart)}}px,0,0); }
                    8% { opacity: .68; }
                    92% { opacity: .5; }
                    100% { opacity: 0; transform: translate3d({{Format(edgeEnd)}}px,0,0); }
                  }
                `;
                document.head.appendChild(style);

                const container = document.createElement('div');
                container.id = 'kk-slide';
                const canvas = document.createElement('canvas');
                canvas.id = 'kk-slide-image';
                canvas.dataset.kkReady = 'false';
                container.appendChild(canvas);
                const edge = document.createElement('div');
                edge.id = 'kk-slide-edge';
                container.appendChild(edge);
                root.appendChild(container);
                window.__kkindleStartSlideOverlay = () => {
                  canvas.dataset.kkStartRequested = 'true';
                  if (canvas.dataset.kkReady !== 'true') return true;
                  container.querySelectorAll('#kk-slide-image, #kk-slide-edge').forEach(node => {
                    node.style.animationPlayState = 'running';
                  });
                  return true;
                };
                const DATA = "{{dataUrl}}";
                const encoded = DATA.slice(DATA.indexOf(',') + 1);
                const raw = atob(encoded);
                const bytes = new Uint8Array(raw.length);
                for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
                createImageBitmap(new Blob([bytes], { type: 'image/png' })).then(bitmap => {
                  canvas.width = bitmap.width;
                  canvas.height = bitmap.height;
                  canvas.getContext('2d', { alpha: false }).drawImage(bitmap, 0, 0);
                  bitmap.close();
                  canvas.dataset.kkReady = 'true';
                  if (canvas.dataset.kkStartRequested === 'true') {
                    window.__kkindleStartSlideOverlay?.();
                  }
                }).catch(() => {
                  canvas.dataset.kkReady = 'error';
                });
                return true;
              } catch (_) {
                document.getElementById('kk-slide')?.remove();
                document.getElementById('kk-slide-style')?.remove();
                return false;
              }
            })();
            """;
    }

    public static string CreateSlideStartScript() =>
        """
        (() => {
          const slide = document.getElementById('kk-slide');
          if (!slide) return false;
          return window.__kkindleStartSlideOverlay?.() === true;
        })();
        """;

    public static string CreateSlideCleanupScript() =>
        """
        (() => {
          document.getElementById('kk-slide')?.remove();
          document.getElementById('kk-slide-style')?.remove();
          delete window.__kkindleStartSlideOverlay;
          return true;
        })();
        """;

    private static string CreateViewTransitionStartScript(
        bool forward,
        int durationMs,
        bool wave)
    {
        var duration = Math.Clamp(durationMs, 180, 1600);
        var slideOrigin = forward ? "100%" : "-100%";
        var slideShadow = forward ? "-22px" : "22px";
        var wave0 = CreateWaveClipPolygon(0, forward, showNewPage: true, 0);
        var wave1 = CreateWaveClipPolygon(.18, forward, showNewPage: true, .8);
        var wave2 = CreateWaveClipPolygon(.45, forward, showNewPage: true, 1.7);
        var wave3 = CreateWaveClipPolygon(.72, forward, showNewPage: true, 2.5);
        var wave4 = CreateWaveClipPolygon(.92, forward, showNewPage: true, 3.3);
        var wave5 = CreateWaveClipPolygon(1, forward, showNewPage: true, 0);
        var animationCss = wave
            ? $$"""
              ::view-transition-old(root) {
                z-index: 1; mix-blend-mode: normal;
                animation: kk-kindle-old {{duration}}ms linear both;
              }
              ::view-transition-new(root) {
                z-index: 2; mix-blend-mode: normal;
                animation: kk-kindle-new {{duration}}ms linear both;
              }
              @keyframes kk-kindle-old {
                0% { filter: brightness(1) contrast(1); }
                32% { filter: grayscale(1) brightness(.86) contrast(1.24); }
                68% { filter: grayscale(.72) brightness(.94) contrast(1.12); }
                100% { filter: none; }
              }
              @keyframes kk-kindle-new {
                0% { clip-path: {{wave0}}; filter: grayscale(1) brightness(.82) contrast(1.3) drop-shadow({{slideShadow}} 0 16px rgba(0,0,0,.42)); }
                18% { clip-path: {{wave1}}; filter: grayscale(1) brightness(.88) contrast(1.24) drop-shadow({{slideShadow}} 0 18px rgba(0,0,0,.38)); }
                45% { clip-path: {{wave2}}; filter: grayscale(.9) brightness(.94) contrast(1.18) drop-shadow({{slideShadow}} 0 20px rgba(0,0,0,.34)); }
                72% { clip-path: {{wave3}}; filter: grayscale(.55) brightness(.97) contrast(1.1) drop-shadow({{slideShadow}} 0 16px rgba(0,0,0,.26)); }
                92% { clip-path: {{wave4}}; filter: grayscale(.18) brightness(1) contrast(1.03) drop-shadow({{slideShadow}} 0 10px rgba(0,0,0,.14)); }
                100% { clip-path: {{wave5}}; filter: none; }
              }
              """
            : $$"""
              ::view-transition-old(root) {
                z-index: 1; mix-blend-mode: normal; animation: none;
              }
              ::view-transition-new(root) {
                z-index: 2; mix-blend-mode: normal;
                animation: kk-slide-new {{duration}}ms cubic-bezier(.35,0,.18,1) both;
              }
              @keyframes kk-slide-new {
                0% { opacity: 1; transform: translate3d({{slideOrigin}},0,0); box-shadow: {{slideShadow}} 0 34px rgba(0,0,0,.2); }
                100% { opacity: 1; transform: translate3d(0,0,0); box-shadow: 0 0 0 rgba(0,0,0,0); }
              }
              """;

        return $$"""
            (() => {
              try {
                if (typeof document.startViewTransition !== 'function'
                    || window.__kkindleViewTransition) return false;
                document.getElementById('kk-view-transition-style')?.remove();
                const style = document.createElement('style');
                style.id = 'kk-view-transition-style';
                style.textContent = `
                  ::view-transition-group(root) { animation-duration: {{duration}}ms; }
                  ::view-transition-image-pair(root) { isolation: isolate; }
                  {{animationCss}}
                `;
                document.head.appendChild(style);

                let releaseUpdate;
                const updateGate = new Promise(resolve => { releaseUpdate = resolve; });
                window.__kkindleViewTransitionReady = false;
                window.__kkindleViewTransitionRelease = () => {
                  if (!releaseUpdate) return false;
                  const release = releaseUpdate;
                  releaseUpdate = null;
                  release();
                  return true;
                };
                const transition = document.startViewTransition(async () => {
                  window.__kkindleViewTransitionReady = true;
                  await updateGate;
                });
                window.__kkindleViewTransition = transition;
                transition.finished.catch(() => {});
                return true;
              } catch (_) {
                document.getElementById('kk-view-transition-style')?.remove();
                delete window.__kkindleViewTransition;
                delete window.__kkindleViewTransitionReady;
                delete window.__kkindleViewTransitionRelease;
                return false;
              }
            })();
            """;
    }

    private static string CreateWaveClipPolygon(
        double progress,
        bool forward,
        bool showNewPage,
        double phase)
    {
        progress = Math.Clamp(progress, 0, 1);
        var boundary = forward ? 100 * (1 - progress) : 100 * progress;
        var amplitude = 5.2 * Math.Sin(Math.PI * progress);
        var boundaryPoints = new string[WavePointCount];
        for (var index = 0; index < WavePointCount; index++)
        {
            var y = index * 100d / (WavePointCount - 1);
            var wave = Math.Sin(index * Math.PI * 3.3 / (WavePointCount - 1) + phase);
            var x = Math.Clamp(boundary + amplitude * wave, 0, 100);
            boundaryPoints[index] = $"{Format(x)}% {Format(y)}%";
        }

        var leftRegion = showNewPage ? !forward : forward;
        var polygon = new StringBuilder("polygon(");
        polygon.Append(leftRegion ? "0% 0%, 0% 100%" : "100% 0%, 100% 100%");
        for (var index = WavePointCount - 1; index >= 0; index--)
            polygon.Append(", ").Append(boundaryPoints[index]);
        return polygon.Append(')').ToString();
    }

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
