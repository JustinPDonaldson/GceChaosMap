/*
 * Chooses where the page gets its fleet from. Both backends expose the same three things:
 *   getConfig()                      -> { mode, chaosEnabled, minHealthyPercent, cities, regions }
 *   start(onFrame, onStatus)         -> begins delivering frames
 *   act(kind, body)                  -> { status, body: { ok, message } }
 *
 *  - server: the ASP.NET app (SignalR frames + /api/chaos endpoints), simulated or live Compute Engine.
 *  - local:  the in-browser simulation in sim.js. Needs no server, so it runs on GitHub Pages.
 *
 * Use ?mode=sim to force the in-browser simulation. Otherwise the server is used if one answers.
 */
(function () {
  'use strict';

  function createServerBackend(config) {
    return {
      kind: 'server',

      getConfig: async () => config ?? (await fetch('api/config')).json(),

      start(onFrame, onStatus) {
        const conn = new signalR.HubConnectionBuilder()
          .withUrl('hubs/fleet')
          .withAutomaticReconnect([0, 1000, 3000, 5000, 10000])
          .build();

        conn.on('state', onFrame);
        conn.onreconnecting(() => onStatus(false, 'reconnecting'));
        conn.onreconnected(() => onStatus(true, 'live'));
        conn.onclose(() => onStatus(false, 'offline'));

        conn.start().then(() => onStatus(true, 'live')).catch((err) => {
          console.error(err);
          onStatus(false, 'offline');
        });
      },

      async act(kind, body) {
        try {
          const res = await fetch('api/chaos/' + kind, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body ?? {}),
          });
          if (res.status === 429) {
            return { status: 429, body: { ok: false, message: 'Easy there. Too many actions from your connection.' } };
          }
          const json = await res.json().catch(() => null);
          return { status: res.status, body: json ?? { ok: false, message: 'Request failed' } };
        } catch {
          return { status: 0, body: { ok: false, message: 'Could not reach the server.' } };
        }
      },
    };
  }

  function createLocalBackend() {
    return new ChaosSim.LocalBackend(CHAOS_SIM_CONFIG);
  }

  async function choose() {
    // GitHub Pages is static hosting: there is never an API, so skip the probe (and its 404 in the console).
    if (new URLSearchParams(location.search).get('mode') === 'sim' || location.hostname.endsWith('.github.io')) {
      return createLocalBackend();
    }

    try {
      // Relative URL on purpose: on GitHub Pages the site lives under /<repo>/, and there is no API there (404).
      const res = await fetch('api/config', { cache: 'no-store' });
      const isJson = (res.headers.get('content-type') || '').includes('json');
      if (res.ok && isJson) return createServerBackend(await res.json());
    } catch {
      // No server reachable: fall through to the in-browser simulation.
    }
    return createLocalBackend();
  }

  window.ChaosBackends = { choose };
})();
