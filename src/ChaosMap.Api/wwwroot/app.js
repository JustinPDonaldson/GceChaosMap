(() => {
  'use strict';

  const W = 1000, H = 520;
  const CARD = { w: 178, h: 108, head: 26, zoneTop: 32, zoneH: 15, cell: 15, cols: 3, footer: 14 };
  const AUTO_CHAOS_MS = 6000;
  const reducedMotion = matchMedia('(prefers-reduced-motion: reduce)').matches;

  const $ = (id) => document.getElementById(id);
  const svg = d3.select('#map');
  const canvas = $('fx');
  const ctx = canvas.getContext('2d');

  let backend = null;      // server (SignalR + REST) or the in-browser simulation, see backends.js
  let config = null;
  let projection = null;
  let regionPos = {};      // region id -> {x, y} pin position in map units
  let latest = null;
  let autoTimer = null;
  const particles = [];
  const ripples = [];
  const HISTORY = 120;
  const history = { ok: [], p95: [] };

  // ---------- boot ----------
  init().catch((err) => {
    console.error(err);
    toast('Could not start: ' + err.message, 'warn');
  });

  async function init() {
    backend = await ChaosBackends.choose();
    config = await backend.getConfig();

    const modeEl = $('mode');
    modeEl.textContent = config.mode === 'gce' ? 'Live · Compute Engine' : config.local ? 'Simulation · in your browser' : 'Simulation';
    modeEl.classList.add(config.mode === 'gce' ? 'gce' : 'sim');
    $('floor').textContent = config.minHealthyPercent;
    if (!config.chaosEnabled) {
      $('auto').disabled = true;
      $('reset').disabled = true;
    }

    projection = d3.geoNaturalEarth1().fitExtent([[6, 6], [W - 6, H - 6]], { type: 'Sphere' });
    await drawBaseMap();
    buildCards();
    sizeCanvas();
    addEventListener('resize', sizeCanvas);
    requestAnimationFrame(frame);
    wireControls();
    connect();
  }

  // ---------- map ----------
  async function drawBaseMap() {
    const path = d3.geoPath(projection);
    svg.append('path').attr('class', 'graticule').attr('d', path(d3.geoGraticule10()));

    try {
      const world = await (await fetch('https://cdn.jsdelivr.net/npm/world-atlas@2.0.2/land-110m.json')).json();
      svg.append('path').attr('class', 'land').attr('d', path(topojson.feature(world, world.objects.land)));
    } catch {
      // The map still works without land outlines; the graticule and pins carry it.
    }

    const cities = svg.append('g').attr('aria-hidden', 'true')
      .selectAll('g.city-group').data(config.cities).join('g').attr('class', 'city-group');
    cities.append('circle').attr('class', 'city').attr('r', 1.8)
      .attr('cx', (c) => projection([c.lon, c.lat])[0]).attr('cy', (c) => projection([c.lon, c.lat])[1]);
    cities.append('text').attr('class', 'city-label')
      .attr('x', (c) => projection([c.lon, c.lat])[0] + 4).attr('y', (c) => projection([c.lon, c.lat])[1] - 3)
      .text((c) => c.name);

    config.regions.forEach((r) => {
      const [x, y] = projection([r.lon, r.lat]);
      regionPos[r.id] = { x, y };
    });
  }

  let layout = {};         // region id -> {x, y} top-left of its card

  const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

  // Give every region the nearest free corner of the map, so cards never sit on top of the land they describe.
  // A brute-force search over one-to-one assignments is fine: there are at most four corners.
  function assignCorners(regions) {
    const m = 8;
    const corners = [
      { x: m, y: m },
      { x: W - CARD.w - m, y: m },
      { x: m, y: H - CARD.h - m },
      { x: W - CARD.w - m, y: H - CARD.h - m },
    ];
    const cost = (r, c) => Math.hypot(regionPos[r.id].x - (c.x + CARD.w / 2), regionPos[r.id].y - (c.y + CARD.h / 2));
    const n = Math.min(regions.length, corners.length);
    const used = new Array(corners.length).fill(false);
    let best = { total: Infinity, pick: [] };

    (function search(i, total, pick) {
      if (total >= best.total) return;
      if (i === n) { best = { total, pick: pick.slice() }; return; }
      for (let c = 0; c < corners.length; c++) {
        if (used[c]) continue;
        used[c] = true; pick.push(c);
        search(i + 1, total + cost(regions[i], corners[c]), pick);
        pick.pop(); used[c] = false;
      }
    })(0, 0, []);

    const out = {};
    regions.slice(0, n).forEach((r, i) => { out[r.id] = corners[best.pick[i]]; });
    // A fifth region or more has no corner left: sit it just below its own pin.
    regions.slice(n).forEach((r) => {
      const p = regionPos[r.id];
      out[r.id] = { x: clamp(p.x - CARD.w / 2, 6, W - CARD.w - 6), y: clamp(p.y + 22, 4, H - CARD.h - 4) };
    });
    return out;
  }

  // Midpoint of whichever side of the card is closest to the pin: the line leaves the card from that edge.
  function edgeAnchor(origin, pin) {
    const sides = [
      { x: origin.x + CARD.w / 2, y: origin.y },
      { x: origin.x + CARD.w / 2, y: origin.y + CARD.h },
      { x: origin.x, y: origin.y + CARD.h / 2 },
      { x: origin.x + CARD.w, y: origin.y + CARD.h / 2 },
    ];
    return sides.reduce((a, b) => (Math.hypot(a.x - pin.x, a.y - pin.y) <= Math.hypot(b.x - pin.x, b.y - pin.y) ? a : b));
  }

  const cardOrigin = (region) => layout[region.id];

  function buildCards() {
    layout = assignCorners(config.regions);
    const layer = svg.append('g').attr('id', 'cards');

    const cards = layer.selectAll('g.card').data(config.regions, (r) => r.id).join('g')
      .attr('class', 'card')
      .attr('id', (r) => 'card-' + r.id)
      .attr('transform', (r) => { const o = cardOrigin(r); return `translate(${o.x},${o.y})`; });

    // stem and pin are drawn in map space, so they live outside the translated card
    const pins = layer.selectAll('g.pinset').data(config.regions, (r) => r.id).join('g').attr('class', 'pinset');
    pins.append('line').attr('class', 'stem').attr('id', (r) => 'stem-' + r.id).each(function (r) {
      const p = regionPos[r.id]; const a = edgeAnchor(cardOrigin(r), p);
      d3.select(this).attr('x1', a.x).attr('y1', a.y).attr('x2', p.x).attr('y2', p.y);
    });
    pins.append('circle').attr('class', 'pin-ring').attr('id', (r) => 'ring-' + r.id).attr('r', 9)
      .attr('cx', (r) => regionPos[r.id].x).attr('cy', (r) => regionPos[r.id].y);
    pins.append('circle').attr('class', 'pin').attr('id', (r) => 'pin-' + r.id).attr('r', 4)
      .attr('cx', (r) => regionPos[r.id].x).attr('cy', (r) => regionPos[r.id].y);
    layer.selectAll('g.card').raise();

    cards.append('rect').attr('class', 'card-bg').attr('width', CARD.w).attr('height', CARD.h);
    cards.append('text').attr('class', 'card-title').attr('x', 10).attr('y', 16).text((r) => r.name);
    cards.append('text').attr('class', 'card-sub').attr('x', 10).attr('y', 24).text((r) => r.id).attr('dy', 0);
    cards.append('text').attr('class', 'card-count').attr('x', CARD.w - 10).attr('y', 17).text('–');
    cards.append('g').attr('class', 'zones');

    const fail = cards.append('g').attr('class', 'fail-region').attr('role', 'button').attr('tabindex', 0)
      .attr('aria-label', (r) => `Fail entire region ${r.id}`)
      .on('click', (_, r) => post('region', { region: r.id }))
      .on('keydown', (e, r) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); post('region', { region: r.id }); } });
    fail.append('rect').attr('x', 8).attr('y', CARD.h - CARD.footer - 4).attr('width', CARD.w - 16).attr('height', CARD.footer + 0);
    fail.append('text').attr('x', CARD.w / 2).attr('y', CARD.h - 8).text('Fail entire region');
  }

  // ---------- state rendering ----------
  function render(update) {
    latest = update;
    const fleet = update.fleet;
    const byId = new Map(fleet.regions.map((r) => [r.id, r]));

    d3.selectAll('g.card').each(function (cfg) {
      const region = byId.get(cfg.id);
      if (!region) return;
      const card = d3.select(this);
      const health = region.healthyCount === 0 ? 'empty' : region.healthyCount >= region.targetSize ? 'full' : 'partial';
      card.attr('class', 'card ' + health);
      card.select('.card-count').text(`${region.healthyCount}/${region.targetSize}`);
      d3.select('#pin-' + cfg.id).attr('class', 'pin ' + health);
      d3.select('#ring-' + cfg.id).attr('class', 'pin-ring ' + health);
      d3.select('#stem-' + cfg.id).attr('class', 'stem ' + health);
      renderZones(card, region);
    });

    renderStats(update);
    renderIncidents(update);
  }

  function renderZones(card, region) {
    const n = region.zones.length;
    const colW = (CARD.w - 16) / n;

    const zones = card.select('.zones').selectAll('g.zone').data(region.zones, (z) => z.id);
    const enter = zones.enter().append('g').attr('class', 'zone');

    const head = enter.append('g').attr('class', 'zone-head').attr('role', 'button').attr('tabindex', 0)
      .attr('aria-label', (z) => `Fail zone ${z.id}`)
      .on('click', (_, z) => post('zone', { zone: z.id }))
      .on('keydown', (e, z) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); post('zone', { zone: z.id }); } });
    head.append('rect').attr('height', CARD.zoneH).attr('width', colW - 4);
    head.append('text').attr('class', 'zone-label').attr('x', (colW - 4) / 2).attr('y', 11);
    head.append('title');
    enter.append('text').attr('class', 'zone-down').attr('x', (colW - 4) / 2).attr('y', CARD.zoneH + 22).text('DOWN');
    enter.append('g').attr('class', 'vms');

    const all = enter.merge(zones);
    all.attr('class', (z) => 'zone' + (z.down ? ' down' : ''))
      .attr('transform', (_, i) => `translate(${8 + i * colW},${CARD.zoneTop})`);
    all.select('.zone-label').text((z) => 'zone ' + z.id.slice(region.id.length + 1) + (z.down ? ' ✕' : ''));
    all.select('.zone-head title').text((z) => `Fail ${z.id} (${z.instances.length} VMs)`);
    all.select('.zone-down').attr('x', (colW - 4) / 2);

    all.each(function (z) {
      const cols = Math.max(1, Math.floor((colW - 4) / CARD.cell));
      d3.select(this).select('.vms').selectAll('rect.vm').data(z.instances, (i) => i.name)
        .join(
          (e) => e.append('rect').attr('class', 'vm enter').attr('width', 13).attr('height', 13)
            .attr('role', 'button').attr('tabindex', 0)
            .on('click', (_, i) => post('instance', { name: i.name }))
            .on('keydown', (ev, i) => { if (ev.key === 'Enter' || ev.key === ' ') { ev.preventDefault(); post('instance', { name: i.name }); } })
            .call((s) => s.append('title')),
          (u) => u,
          (x) => x.remove()
        )
        .attr('x', (_, k) => (k % cols) * CARD.cell)
        .attr('y', (_, k) => CARD.zoneH + 6 + Math.floor(k / cols) * CARD.cell)
        .attr('class', (i) => 'vm ' + i.state.toLowerCase())
        .attr('aria-label', (i) => `Delete ${i.name}, ${i.state}`)
        .select('title').text((i) => `${i.name}\n${i.zone}\n${i.state}\nClick to delete`);
    });

    zones.exit().remove();
  }

  function renderStats(update) {
    const { fleet, traffic } = update;
    const healthy = fleet.regions.reduce((s, r) => s + r.healthyCount, 0);
    const target = fleet.regions.reduce((s, r) => s + r.targetSize, 0);

    setKpi('kHealthy', `${healthy}/${target}`, healthy === target ? 'good' : healthy < target / 2 ? 'bad' : 'warn');
    if (traffic.total === 0) {
      setKpi('kSuccess', '–'); setKpi('kP50', '–'); setKpi('kP95', '–');
    } else {
      const pct = traffic.successRate * 100;
      setKpi('kSuccess', pct.toFixed(pct === 100 ? 0 : 1) + '%', pct >= 99.5 ? 'good' : pct >= 90 ? 'warn' : 'bad');
      setKpi('kP50', traffic.p50Ms + ' ms');
      setKpi('kP95', traffic.p95Ms + ' ms');
    }

    pushHistory(history.ok, traffic.total === 0 ? 1 : traffic.successRate);
    pushHistory(history.p95, traffic.p95Ms);
    drawSpark('sparkOk', history.ok, 0, 1);
    drawSpark('sparkLat', history.p95, 0, Math.max(200, ...history.p95));

    const ul = d3.select('#byRegion');
    const total = Object.values(traffic.byRegion).reduce((a, b) => a + b, 0) || 1;
    ul.selectAll('li').data(fleet.regions, (r) => r.id).join(
      (e) => {
        const li = e.append('li');
        li.append('span').attr('class', 'name');
        li.append('span').attr('class', 'track').append('span').attr('class', 'fill');
        li.append('span').attr('class', 'pct');
        return li;
      }
    ).each(function (r) {
      const share = (traffic.byRegion[r.id] || 0) / total;
      const li = d3.select(this);
      li.select('.name').text(r.id);
      li.select('.fill').style('width', (share * 100).toFixed(0) + '%');
      li.select('.pct').text((share * 100).toFixed(0) + '%');
    });
  }

  function setKpi(id, text, tone) {
    const el = $(id);
    el.textContent = text;
    el.className = 'v' + (tone ? ' ' + tone : '');
  }

  function pushHistory(arr, v) {
    arr.push(v);
    if (arr.length > HISTORY) arr.shift();
  }

  function drawSpark(id, values, min, max) {
    const el = d3.select('#' + id);
    el.classed('lat', id === 'sparkLat');
    if (values.length < 2) return;
    const y = d3.scaleLinear().domain([min, max || 1]).range([40, 4]);
    // The newest sample is pinned to the right edge; older ones scroll off to the left.
    const step = 300 / (HISTORY - 1);
    const x = (_, i) => 300 - (values.length - 1 - i) * step;
    const ln = d3.line().x(x).y((v) => y(v));
    const ar = d3.area().x(x).y0(42).y1((v) => y(v));
    el.selectAll('path.area').data([values]).join('path').attr('class', 'area').attr('d', ar);
    el.selectAll('path.line').data([values]).join('path').attr('class', 'line').attr('d', ln);
  }

  function renderIncidents(update) {
    const now = Date.parse(update.fleet.at);
    const items = update.incidents;
    $('noInc').style.display = items.length ? 'none' : '';

    d3.select('#incidents').selectAll('li').data(items, (i) => i.id).join(
      (e) => {
        const li = e.append('li');
        li.append('span').attr('class', 'pip');
        const what = li.append('div').attr('class', 'what');
        what.append('b'); what.append('span');
        li.append('span').attr('class', 't');
        return li;
      }
    ).each(function (inc) {
      const li = d3.select(this);
      const done = inc.recoveredAt != null;
      li.classed('done', done);
      li.select('b').text(`${cap(inc.kind)} killed`);
      li.select('.what span').text(inc.target);
      const secs = done ? inc.recoverySeconds : (now - Date.parse(inc.startedAt)) / 1000;
      li.select('.t').text(done ? `whole again in ${secs.toFixed(1)}s` : `recovering… ${Math.max(0, secs).toFixed(0)}s`);
    }).order();
  }

  const cap = (s) => s.charAt(0).toUpperCase() + s.slice(1);

  // ---------- request animation ----------
  function sizeCanvas() {
    const box = canvas.getBoundingClientRect();
    const dpr = window.devicePixelRatio || 1;
    canvas.width = Math.round(box.width * dpr);
    canvas.height = Math.round(box.height * dpr);
    canvas._scale = (box.width / W) * dpr;
  }

  function spawn(requests) {
    if (document.hidden) return;
    const now = performance.now();
    for (const r of requests) {
      if (reducedMotion && Math.random() > 0.25) continue;
      const from = projection([r.lon, r.lat]);
      const to = r.region ? regionPos[r.region] : null;
      // With every region down the balancer answers 503 itself: the request dies partway.
      const target = to ?? { x: from[0] + (W / 2 - from[0]) * 0.25, y: from[1] + (H / 2 - from[1]) * 0.25 };
      const dx = target.x - from[0], dy = target.y - from[1];
      const dist = Math.hypot(dx, dy);
      particles.push({
        x0: from[0], y0: from[1], x1: target.x, y1: target.y,
        cx: (from[0] + target.x) / 2, cy: (from[1] + target.y) / 2 - dist * 0.22,
        t0: now + Math.random() * 450,
        dur: Math.min(1500, 450 + dist * 1.6),
        kind: !r.ok ? 'fail' : r.failover ? 'failover' : 'ok',
        stopAt: to ? 1 : 0.6,
      });
    }
    if (particles.length > 500) particles.splice(0, particles.length - 500);
  }

  const COLORS = { ok: '61,220,151', failover: '245,185,66', fail: '255,93,108' };

  function frame(now) {
    const s = canvas._scale || 1;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.setTransform(s, 0, 0, s, 0, 0);

    for (let i = particles.length - 1; i >= 0; i--) {
      const p = particles[i];
      if (now < p.t0) continue;
      const t = (now - p.t0) / p.dur;
      if (t >= p.stopAt) {
        const at = bez(p, p.stopAt);
        ripples.push({ x: at.x, y: at.y, t0: now, kind: p.kind });
        particles.splice(i, 1);
        continue;
      }
      const c = COLORS[p.kind];
      for (let k = 0; k < 4; k++) {
        const tt = Math.max(0, t - k * 0.035);
        const q = bez(p, tt);
        ctx.fillStyle = `rgba(${c},${0.9 - k * 0.22})`;
        ctx.beginPath();
        ctx.arc(q.x, q.y, 2.1 - k * 0.3, 0, Math.PI * 2);
        ctx.fill();
      }
    }

    for (let i = ripples.length - 1; i >= 0; i--) {
      const r = ripples[i];
      const t = (now - r.t0) / 420;
      if (t >= 1) { ripples.splice(i, 1); continue; }
      ctx.strokeStyle = `rgba(${COLORS[r.kind]},${0.7 * (1 - t)})`;
      ctx.lineWidth = 1.2;
      ctx.beginPath();
      ctx.arc(r.x, r.y, 3 + t * (r.kind === 'fail' ? 12 : 7), 0, Math.PI * 2);
      ctx.stroke();
    }

    requestAnimationFrame(frame);
  }

  function bez(p, t) {
    const u = 1 - t;
    return {
      x: u * u * p.x0 + 2 * u * t * p.cx + t * t * p.x1,
      y: u * u * p.y0 + 2 * u * t * p.cy + t * t * p.y1,
    };
  }

  // ---------- backend ----------
  function connect() {
    const status = (on, text) => {
      const el = $('conn');
      el.textContent = text;
      el.classList.toggle('on', on);
      el.classList.toggle('off', !on);
    };

    backend.start((update) => {
      render(update);
      spawn(update.requests);
    }, status);
  }

  async function post(kind, body) {
    const { body: result } = await backend.act(kind, body);
    toast(result?.message ?? 'Request failed', result?.ok ? 'ok' : 'warn');
  }

  // ---------- controls ----------
  function wireControls() {
    $('reset').addEventListener('click', () => {
      history.ok.length = 0; history.p95.length = 0;
      post('reset');
    });

    $('auto').addEventListener('click', () => {
      const on = !autoTimer;
      $('auto').setAttribute('aria-pressed', String(on));
      $('auto').textContent = 'Auto-chaos: ' + (on ? 'on' : 'off');
      if (on) {
        autoChaos();
        autoTimer = setInterval(autoChaos, AUTO_CHAOS_MS);
      } else {
        clearInterval(autoTimer);
        autoTimer = null;
      }
    });
  }

  function autoChaos() {
    if (!latest) return;
    const healthy = latest.fleet.regions
      .flatMap((r) => r.zones.flatMap((z) => z.instances))
      .filter((i) => i.state === 'Healthy');
    if (healthy.length === 0) return;
    const victim = healthy[Math.floor(Math.random() * healthy.length)];
    post('instance', { name: victim.name });
  }

  let toastTimer = null;
  function toast(message, tone) {
    const el = $('toast');
    el.textContent = message;
    el.className = 'toast show ' + (tone || '');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove('show'), 3200);
  }
})();
