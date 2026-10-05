/*
 * In-browser port of the C# simulation (SimulatedFleet, TrafficSimulator, IncidentTracker, ChaosGuard and the
 * endpoint rules in Program.cs). It lets the site run on static hosting such as GitHub Pages with no server.
 *
 * Loads in the browser (window.ChaosSim) and in Node (require) so the same file can be unit tested.
 * Keep it in step with src/ChaosMap.Api/Providers/SimulatedFleet.cs; tests/js/sim.test.js compares the two configs.
 */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.ChaosSim = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  const NAME_ALPHABET = 'bcdfghjklmnpqrstvwxz';
  const STEP_MS = 250;                    // simulation slice, same as the C# model
  const MAX_CATCH_UP_MS = 10 * 60 * 1000; // a tab asleep for an hour need not replay every slice
  const TRAFFIC_WINDOW_MS = 10_000;
  const MAX_INCIDENTS = 12;
  const INCIDENT_GRACE_MS = 15_000;

  const result = (ok, message, affected = 0) => ({ ok, message, affected });

  // ---------- fleet ----------

  /**
   * One regional managed instance group per region. A deleted VM is noticed after a delay, the group recreates it in
   * the least-loaded healthy zone, and a new VM walks Provisioning -> Staging -> Starting (health check) -> Healthy.
   * State advances from timestamps, in small slices, so results do not depend on how often it is polled.
   */
  class SimulatedFleet {
    constructor(config, clock = Date.now, rng = Math.random) {
      this.config = config;
      this.sim = config.sim;
      this.clock = clock;
      this.rng = rng;
      this.regions = [];
      this.cursor = 0;
      this.buildSteadyState();
    }

    get mode() { return 'simulation'; }

    refresh() {
      const now = this._catchUp();
      return this._snapshot(now);
    }

    killInstance(name) {
      const now = this._catchUp();
      for (const region of this.regions) {
        const vm = region.vms.find((v) => v.name === name);
        if (!vm) continue;
        if (vm.state === 'Stopping') return result(false, `${name} is already stopping.`);
        this._stop(vm, now);
        region.missingSince ??= now;
        return result(true, `Deleted ${name} in ${vm.zone}.`, 1);
      }
      return result(false, `No instance named ${name}.`);
    }

    killZone(zoneId) {
      const now = this._catchUp();
      for (const region of this.regions) {
        const zone = region.zones.find((z) => zoneIdOf(region, z) === zoneId);
        if (!zone) continue;
        if (zone.down) return result(false, `${zoneId} is already down.`);
        const affected = this._takeZoneDown(region, zone, now);
        return result(true, `Zone ${zoneId} failed; ${affected} instances lost.`, affected);
      }
      return result(false, `No zone named ${zoneId}.`);
    }

    killRegion(regionId) {
      const now = this._catchUp();
      const region = this.regions.find((r) => r.def.id === regionId);
      if (!region) return result(false, `No region named ${regionId}.`);

      let affected = 0;
      for (const zone of region.zones.filter((z) => !z.down)) affected += this._takeZoneDown(region, zone, now);
      return affected === 0
        ? result(false, `${regionId} is already down.`)
        : result(true, `Region ${regionId} failed; ${affected} instances lost.`, affected);
    }

    reset() {
      this.buildSteadyState();
      return result(true, 'Fleet restored to steady state.');
    }

    buildSteadyState() {
      const now = this.clock();
      this.cursor = now;
      this.regions = this.config.regions.map((def) => {
        const region = { def, zones: def.zones.map((suffix) => ({ suffix, down: false, downUntil: 0 })), vms: [], missingSince: null };
        for (let i = 0; i < this.config.targetPerRegion; i++) {
          region.vms.push(this._newVm(region, region.zones[i % region.zones.length], 'Healthy', now - 60_000));
        }
        return region;
      });
    }

    _takeZoneDown(region, zone, now) {
      zone.down = true;
      zone.downUntil = now + this.sim.zoneOutageSeconds * 1000;

      const id = zoneIdOf(region, zone);
      let affected = 0;
      for (const vm of region.vms.filter((v) => v.zone === id && v.state !== 'Stopping')) {
        this._stop(vm, now);
        affected++;
      }
      region.missingSince ??= now;
      return affected;
    }

    _stop(vm, now) {
      vm.state = 'Stopping';
      vm.since = now;
    }

    _catchUp() {
      const now = this.clock();
      if (now - this.cursor > MAX_CATCH_UP_MS) this.cursor = now - MAX_CATCH_UP_MS;
      while (this.cursor < now) {
        this.cursor = Math.min(this.cursor + STEP_MS, now);
        for (const region of this.regions) this._advance(region, this.cursor);
      }
      return now;
    }

    _advance(region, now) {
      for (const zone of region.zones) if (zone.down && now >= zone.downUntil) zone.down = false;

      for (const vm of [...region.vms]) {
        // Step by exact boundaries so a long gap still lands in the right state.
        while (vm.state !== 'Healthy') {
          const [next, seconds] = this._nextStage(vm.state);
          const due = vm.since + seconds * 1000;
          if (now < due) break;
          if (vm.state === 'Stopping') {
            region.vms.splice(region.vms.indexOf(vm), 1);
            break;
          }
          vm.state = next;
          vm.since = due;
        }
      }

      this._reconcile(region, now);
    }

    _nextStage(state) {
      switch (state) {
        case 'Provisioning': return ['Staging', this.sim.provisioningSeconds];
        case 'Staging': return ['Starting', this.sim.stagingSeconds];
        case 'Starting': return ['Healthy', this.sim.startingSeconds];
        default: return ['Stopping', this.sim.stoppingSeconds];
      }
    }

    _reconcile(region, now) {
      const live = region.vms.filter((v) => v.state !== 'Stopping').length;
      const deficit = this.config.targetPerRegion - live;
      if (deficit <= 0) {
        region.missingSince = null;
        return;
      }

      region.missingSince ??= now;
      if (now < region.missingSince + this.sim.reconcileDelaySeconds * 1000) return;

      const upZones = region.zones.filter((z) => !z.down);
      if (upZones.length === 0) return; // nowhere to place VMs; keep waiting for a zone to come back

      for (let i = 0; i < deficit; i++) {
        // Regional groups keep zones balanced: place into the zone with the fewest live VMs.
        const counts = upZones.map((z) => region.vms.filter((v) => v.zone === zoneIdOf(region, z) && v.state !== 'Stopping').length);
        const fewest = Math.min(...counts);
        const candidates = upZones.filter((_, k) => counts[k] === fewest);
        const zone = candidates[Math.floor(this.rng() * candidates.length)];
        region.vms.push(this._newVm(region, zone, 'Provisioning', now));
      }
      region.missingSince = null;
    }

    _newVm(region, zone, state, since) {
      let name;
      do {
        let suffix = '';
        for (let i = 0; i < 4; i++) suffix += NAME_ALPHABET[Math.floor(this.rng() * NAME_ALPHABET.length)];
        name = `web-${abbrev(region.def.id)}-${suffix}`;
      } while (this.regions.some((r) => r.vms.some((v) => v.name === name)) || region.vms.some((v) => v.name === name));
      return { name, zone: zoneIdOf(region, zone), state, since };
    }

    _snapshot(now) {
      const regions = this.regions.map((r) => {
        const zones = r.zones.map((z) => {
          const id = zoneIdOf(r, z);
          const instances = r.vms
            .filter((v) => v.zone === id)
            .sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))
            .map((v) => ({ name: v.name, region: r.def.id, zone: v.zone, state: v.state, stateSince: new Date(v.since).toISOString() }));
          return { id, down: z.down, instances };
        });
        return {
          id: r.def.id, name: r.def.name, lat: r.def.lat, lon: r.def.lon,
          targetSize: this.config.targetPerRegion,
          healthyCount: r.vms.filter((v) => v.state === 'Healthy').length,
          zones,
        };
      });
      return { at: new Date(now).toISOString(), mode: this.mode, regions };
    }
  }

  const zoneIdOf = (region, zone) => `${region.def.id}-${zone.suffix}`;

  /** "us-central1" -> "uc1", "europe-west1" -> "ew1". */
  function abbrev(regionId) {
    const parts = regionId.split('-');
    const digits = parts[parts.length - 1].replace(/\D/g, '');
    return parts.map((p) => p[0]).join('') + digits;
  }

  // ---------- traffic ----------

  function haversineKm(lat1, lon1, lat2, lon2) {
    const rad = (d) => (d * Math.PI) / 180;
    const dLat = rad(lat2 - lat1);
    const dLon = rad(lon2 - lon1);
    const a = Math.sin(dLat / 2) ** 2 + Math.cos(rad(lat1)) * Math.cos(rad(lat2)) * Math.sin(dLon / 2) ** 2;
    return 6371 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  }

  /**
   * Routes one request the way a global external load balancer would: to the closest region that still has a healthy
   * backend. A request that lands on a VM already being deleted fails (the balancer has not noticed yet).
   */
  function routeRequest(city, fleet, rng) {
    const byDistance = [...fleet.regions].sort(
      (a, b) => haversineKm(city.lat, city.lon, a.lat, a.lon) - haversineKm(city.lat, city.lon, b.lat, b.lon));
    const nearest = byDistance[0];
    const chosen = byDistance.find((r) => r.healthyCount > 0);
    const base = { city: city.name, lat: city.lat, lon: city.lon };

    // Every region is out of healthy backends: the balancer answers 503 itself.
    if (!chosen || !nearest) return { ...base, region: null, latencyMs: 0, ok: false, failover: false };

    const backends = chosen.zones.flatMap((z) => z.instances).filter((i) => i.state === 'Healthy' || i.state === 'Stopping');
    const target = backends[Math.floor(rng() * backends.length)];
    const failover = chosen.id !== nearest.id;

    if (target.state === 'Stopping') return { ...base, region: chosen.id, latencyMs: 0, ok: false, failover };

    const km = haversineKm(city.lat, city.lon, chosen.lat, chosen.lon);
    const latencyMs = Math.round(15 + km * 0.02 + rng() * 8);
    return { ...base, region: chosen.id, latencyMs, ok: true, failover };
  }

  const percentile = (sorted, p) =>
    sorted.length === 0 ? 0 : sorted[Math.min(sorted.length - 1, Math.ceil(p * sorted.length) - 1)];

  class TrafficSimulator {
    constructor(cities, clock = Date.now, rng = Math.random) {
      this.cities = cities;
      this.totalWeight = cities.reduce((s, c) => s + c.weight, 0);
      this.clock = clock;
      this.rng = rng;
      this.samples = [];
    }

    generate(fleet, count) {
      if (this.cities.length === 0) return [];
      const now = this.clock();
      const events = [];
      for (let i = 0; i < count; i++) {
        const ev = routeRequest(this._pickCity(), fleet, this.rng);
        events.push(ev);
        this.samples.push({ at: now, ok: ev.ok, latencyMs: ev.latencyMs, region: ev.region });
      }
      return events;
    }

    stats() {
      const cutoff = this.clock() - TRAFFIC_WINDOW_MS;
      while (this.samples.length > 0 && this.samples[0].at < cutoff) this.samples.shift();

      const total = this.samples.length;
      if (total === 0) return { total: 0, successRate: 1, p50Ms: 0, p95Ms: 0, byRegion: {} };

      const ok = this.samples.filter((s) => s.ok);
      const latencies = ok.map((s) => s.latencyMs).sort((a, b) => a - b);
      const byRegion = {};
      for (const s of ok) if (s.region !== null) byRegion[s.region] = (byRegion[s.region] ?? 0) + 1;

      return { total, successRate: ok.length / total, p50Ms: percentile(latencies, 0.5), p95Ms: percentile(latencies, 0.95), byRegion };
    }

    _pickCity() {
      let roll = this.rng() * this.totalWeight;
      for (const city of this.cities) {
        roll -= city.weight;
        if (roll <= 0) return city;
      }
      return this.cities[this.cities.length - 1];
    }
  }

  // ---------- incidents ----------

  /** An incident opens when chaos is injected and closes when every region it touched is fully healthy again. */
  class IncidentTracker {
    constructor(clock = Date.now) {
      this.clock = clock;
      this.incidents = [];
      this.sequence = 0;
    }

    start(kind, target, regionIds) {
      this.incidents.push({
        id: `inc-${++this.sequence}`, seq: this.sequence, kind, target,
        startedAt: this.clock(), regionIds: [...new Set(regionIds)], seenDegraded: false, recoveredAt: null,
      });
      if (this.incidents.length > MAX_INCIDENTS) this.incidents.shift();
    }

    observe(fleet) {
      const now = this.clock();
      for (const inc of this.incidents.filter((i) => i.recoveredAt === null)) {
        const regions = fleet.regions.filter((r) => inc.regionIds.includes(r.id));
        const full = regions.length > 0 && regions.every((r) => r.healthyCount >= r.targetSize);

        if (!full) inc.seenDegraded = true;
        else if (inc.seenDegraded || now - inc.startedAt > INCIDENT_GRACE_MS) inc.recoveredAt = now;
      }
    }

    clear() { this.incidents = []; }

    /** Newest first. */
    recent() {
      return [...this.incidents].sort((a, b) => b.seq - a.seq).map((i) => ({
        id: i.id, kind: i.kind, target: i.target,
        startedAt: new Date(i.startedAt).toISOString(),
        recoveredAt: i.recoveredAt === null ? null : new Date(i.recoveredAt).toISOString(),
        recoverySeconds: i.recoveredAt === null ? null : Math.round(((i.recoveredAt - i.startedAt) / 1000) * 10) / 10,
      }));
    }
  }

  // ---------- guard ----------

  const instanceScope = (fleet, name) => {
    for (const r of fleet.regions) for (const z of r.zones) {
      const vm = z.instances.find((i) => i.name === name);
      if (vm) return { kind: 'instance', target: name, regionIds: [r.id], healthyVictims: vm.state === 'Healthy' ? 1 : 0 };
    }
    return null;
  };

  const zoneScope = (fleet, zoneId) => {
    for (const r of fleet.regions) {
      const zone = r.zones.find((z) => z.id === zoneId);
      if (zone) return { kind: 'zone', target: zoneId, regionIds: [r.id], healthyVictims: zone.instances.filter((i) => i.state === 'Healthy').length };
    }
    return null;
  };

  const regionScope = (fleet, regionId) => {
    const region = fleet.regions.find((r) => r.id === regionId);
    return region ? { kind: 'region', target: regionId, regionIds: [region.id], healthyVictims: region.healthyCount } : null;
  };

  /** Kill switch, an action budget, and a floor on how much of the fleet may be down at once. */
  class ChaosGuard {
    constructor(options, clock = Date.now) {
      this.options = options;
      this.clock = clock;
      this.actions = [];
    }

    get enabled() { return this.options.enabled; }
    get minGlobalHealthyPercent() { return this.options.minGlobalHealthyPercent; }

    /** Returns null when the action may proceed (and counts it), otherwise the reason it was refused. */
    tryAuthorize(fleet, scope) {
      if (!this.options.enabled) return 'Chaos is switched off on this deployment.';

      const now = this.clock();
      while (this.actions.length > 0 && now - this.actions[0] > 60_000) this.actions.shift();
      if (this.actions.length >= this.options.maxActionsPerMinute) return 'Too much chaos right now. Give the fleet a minute.';

      const target = fleet.regions.reduce((s, r) => s + r.targetSize, 0);
      const healthy = fleet.regions.reduce((s, r) => s + r.healthyCount, 0);
      if (target > 0) {
        const percentAfter = (Math.max(0, healthy - scope.healthyVictims) * 100) / target;
        if (percentAfter < this.options.minGlobalHealthyPercent) {
          return `Refused: that would leave only ${Math.round(percentAfter)}% of the fleet healthy (floor is ${this.options.minGlobalHealthyPercent}%).`;
        }
      }

      this.actions.push(now);
      return null;
    }
  }

  // ---------- backend used by the page when there is no server ----------

  /**
   * The browser-side equivalent of FleetLoop + the /api endpoints. Frames have the same shape the SignalR hub sends,
   * and act() mirrors the HTTP status codes of the real endpoints, so app.js treats both backends the same way.
   */
  class LocalBackend {
    constructor(config, { clock = Date.now, rng = Math.random, tickMs = 500, requestsPerTick = 8 } = {}) {
      this.config = config;
      this.kind = 'local';
      this.tickMs = tickMs;
      this.requestsPerTick = requestsPerTick;
      this.fleet = new SimulatedFleet(config, clock, rng);
      this.traffic = new TrafficSimulator(config.cities, clock, rng);
      this.incidents = new IncidentTracker(clock);
      this.guard = new ChaosGuard(config.chaos, clock);
      this.latest = null;
      this.timer = null;
    }

    async getConfig() {
      return {
        mode: 'simulation',
        local: true,
        chaosEnabled: this.guard.enabled,
        minHealthyPercent: this.guard.minGlobalHealthyPercent,
        cities: this.config.cities,
        regions: this.config.regions.map(({ id, name, lat, lon, zones }) => ({ id, name, lat, lon, zones })),
      };
    }

    start(onFrame, onStatus) {
      this.stop();
      onStatus(true, 'simulated');
      const tick = () => onFrame(this.buildFrame());
      tick();
      this.timer = setInterval(tick, this.tickMs);
    }

    stop() {
      if (this.timer !== null) clearInterval(this.timer);
      this.timer = null;
    }

    buildFrame() {
      const fleet = this.fleet.refresh();
      this.incidents.observe(fleet);
      const requests = this.traffic.generate(fleet, this.requestsPerTick);
      this.latest = { fleet, incidents: this.incidents.recent(), requests, traffic: this.traffic.stats() };
      return this.latest;
    }

    /** kind: "instance" | "zone" | "region" | "reset". Resolves to { status, body } like the HTTP endpoint. */
    async act(kind, body = {}) {
      if (kind === 'reset') {
        const reset = this.fleet.reset();
        this.incidents.clear();
        return { status: 200, body: reset };
      }

      const rule = {
        instance: { target: body.name, scopeOf: instanceScope, run: (t) => this.fleet.killInstance(t) },
        zone: { target: body.zone, scopeOf: zoneScope, run: (t) => this.fleet.killZone(t) },
        region: { target: body.region, scopeOf: regionScope, run: (t) => this.fleet.killRegion(t) },
      }[kind];
      if (!rule) return { status: 404, body: result(false, `Unknown action '${kind}'.`) };

      if (!rule.target || !String(rule.target).trim()) return { status: 400, body: result(false, 'A target is required.') };
      if (!this.latest) return { status: 409, body: result(false, 'Fleet is not ready yet.') };

      const scope = rule.scopeOf(this.latest.fleet, rule.target);
      if (!scope) return { status: 404, body: result(false, `Unknown target '${rule.target}'.`) };

      const refusal = this.guard.tryAuthorize(this.latest.fleet, scope);
      if (refusal) return { status: 409, body: result(false, refusal) };

      const outcome = rule.run(rule.target);
      if (outcome.ok) this.incidents.start(scope.kind, scope.target, scope.regionIds);
      return { status: 200, body: outcome };
    }
  }

  return {
    SimulatedFleet, TrafficSimulator, IncidentTracker, ChaosGuard, LocalBackend,
    routeRequest, haversineKm, instanceScope, zoneScope, regionScope,
  };
});
