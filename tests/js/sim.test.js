'use strict';

// Run with: node --test tests/js
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const wwwroot = path.join(__dirname, '..', '..', 'src', 'ChaosMap.Api', 'wwwroot');
const Sim = require(path.join(wwwroot, 'sim.js'));
const siteConfig = require(path.join(wwwroot, 'sim-config.js'));

// ---------- helpers ----------

function mulberry32(seed) {
  let a = seed;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const testConfig = () => ({
  targetPerRegion: 6,
  regions: [
    { id: 'us-central1', name: 'Iowa', lat: 41.26, lon: -95.86, zones: ['a', 'b', 'c'] },
    { id: 'europe-west1', name: 'Belgium', lat: 50.45, lon: 3.82, zones: ['b', 'c', 'd'] },
  ],
  cities: [{ name: 'Chicago', lat: 41.88, lon: -87.63, weight: 1 }],
  sim: { ...siteConfig.sim },
  chaos: { enabled: true, minGlobalHealthyPercent: 34, maxActionsPerMinute: 30 },
});

function world(config = testConfig()) {
  const clock = { now: Date.UTC(2026, 0, 1), advance(seconds) { this.now += seconds * 1000; }, fn() { return this.now; } };
  const fleet = new Sim.SimulatedFleet(config, () => clock.now, mulberry32(42));
  return { clock, fleet, config };
}

const all = (snap) => snap.regions.flatMap((r) => r.zones).flatMap((z) => z.instances);
const inRegion = (snap, id) => snap.regions.find((r) => r.id === id).zones.flatMap((z) => z.instances);
const region = (snap, id) => snap.regions.find((r) => r.id === id);
const Chicago = { name: 'Chicago', lat: 41.88, lon: -87.63, weight: 1 };

// ---------- fleet ----------

test('starts healthy and zone balanced', () => {
  const { fleet } = world();
  for (const r of fleet.refresh().regions) {
    assert.equal(r.healthyCount, 6);
    for (const z of r.zones) assert.equal(z.instances.length, 2);
  }
});

test('a killed instance is replaced and walks the boot states', () => {
  const { fleet, clock } = world();
  const victim = inRegion(fleet.refresh(), 'us-central1')[0];

  assert.equal(fleet.killInstance(victim.name).ok, true);

  let snap = fleet.refresh();
  assert.equal(all(snap).find((i) => i.name === victim.name).state, 'Stopping');
  assert.equal(region(snap, 'us-central1').healthyCount, 5);

  clock.advance(2.6); // autohealing notices, replacement is created
  snap = fleet.refresh();
  assert.ok(!all(snap).some((i) => i.name === victim.name));
  const fresh = inRegion(snap, 'us-central1').filter((i) => i.state === 'Provisioning');
  assert.equal(fresh.length, 1);

  clock.advance(2);
  assert.equal(all(fleet.refresh()).find((i) => i.name === fresh[0].name).state, 'Staging');
  clock.advance(2);
  assert.equal(all(fleet.refresh()).find((i) => i.name === fresh[0].name).state, 'Starting');
  clock.advance(4);
  assert.equal(region(fleet.refresh(), 'us-central1').healthyCount, 6);
});

test('zone failure redistributes capacity to the surviving zones', () => {
  const { fleet, clock } = world();
  const result = fleet.killZone('us-central1-a');
  assert.equal(result.ok, true);
  assert.equal(result.affected, 2);

  clock.advance(20); // long enough to recover capacity, not long enough for the zone to return
  const iowa = region(fleet.refresh(), 'us-central1');

  assert.equal(iowa.healthyCount, 6);
  const down = iowa.zones.find((z) => z.id === 'us-central1-a');
  assert.equal(down.down, true);
  assert.equal(down.instances.length, 0);
  assert.equal(iowa.zones.find((z) => z.id === 'us-central1-b').instances.length, 3);
  assert.equal(iowa.zones.find((z) => z.id === 'us-central1-c').instances.length, 3);
});

test('a failed zone comes back after the outage window', () => {
  const { fleet, clock } = world();
  fleet.killZone('us-central1-a');
  clock.advance(31);
  const zone = region(fleet.refresh(), 'us-central1').zones.find((z) => z.id === 'us-central1-a');
  assert.equal(zone.down, false);
});

test('a failed region has no capacity until a zone returns', () => {
  const { fleet, clock } = world();
  assert.equal(fleet.killRegion('us-central1').affected, 6);

  clock.advance(15);
  let snap = fleet.refresh();
  assert.equal(region(snap, 'us-central1').healthyCount, 0);
  assert.equal(region(snap, 'europe-west1').healthyCount, 6);

  clock.advance(16); // zones return at 30s
  fleet.refresh();
  clock.advance(10); // group rebuilds, health checks pass
  assert.equal(region(fleet.refresh(), 'us-central1').healthyCount, 6);
});

test('catches up correctly after a long idle gap', () => {
  const { fleet, clock } = world();
  const victim = inRegion(fleet.refresh(), 'europe-west1')[0];
  fleet.killInstance(victim.name);

  clock.advance(5 * 60); // nobody watching; a single refresh must land in the final state
  const belgium = region(fleet.refresh(), 'europe-west1');
  assert.equal(belgium.healthyCount, 6);
  assert.equal(belgium.zones.reduce((n, z) => n + z.instances.length, 0), 6);
});

test('rejects unknown and repeated targets', () => {
  const { fleet } = world();
  assert.equal(fleet.killInstance('nope').ok, false);
  assert.equal(fleet.killZone('nope-a').ok, false);
  assert.equal(fleet.killZone('us-central1-a').ok, true);
  assert.equal(fleet.killZone('us-central1-a').ok, false);
});

test('reset restores steady state', () => {
  const { fleet } = world();
  fleet.killRegion('us-central1');
  fleet.reset();
  const snap = fleet.refresh();
  for (const r of snap.regions) assert.equal(r.healthyCount, 6);
  assert.ok(!snap.regions.flatMap((r) => r.zones).some((z) => z.down));
});

test('instance names are unique', () => {
  const { fleet } = world();
  const names = all(fleet.refresh()).map((i) => i.name);
  assert.equal(new Set(names).size, names.length);
});

// ---------- traffic ----------

test('routes to the nearest healthy region', () => {
  const { fleet } = world();
  const ev = Sim.routeRequest(Chicago, fleet.refresh(), mulberry32(1));
  assert.equal(ev.ok, true);
  assert.equal(ev.region, 'us-central1');
  assert.equal(ev.failover, false);
});

test('fails over when the nearest region is down, and pays for it in latency', () => {
  const { fleet, clock } = world();
  const before = Sim.routeRequest(Chicago, fleet.refresh(), mulberry32(1));

  fleet.killRegion('us-central1');
  clock.advance(4); // dead VMs are gone, nothing healthy left in Iowa
  const after = Sim.routeRequest(Chicago, fleet.refresh(), mulberry32(1));

  assert.equal(after.ok, true);
  assert.equal(after.region, 'europe-west1');
  assert.equal(after.failover, true);
  assert.ok(after.latencyMs > before.latencyMs + 50);
});

test('returns 503 when every region is down', () => {
  const { fleet, clock } = world();
  fleet.killRegion('us-central1');
  fleet.killRegion('europe-west1');
  clock.advance(4);

  const ev = Sim.routeRequest(Chicago, fleet.refresh(), mulberry32(1));
  assert.equal(ev.ok, false);
  assert.equal(ev.region, null);
});

test('requests that hit a dying VM fail', () => {
  const { fleet } = world();
  fleet.killZone('us-central1-a');
  const snap = fleet.refresh(); // 2 VMs stopping, 4 still healthy: the balancer has not noticed yet

  const rng = mulberry32(7);
  const results = Array.from({ length: 400 }, () => Sim.routeRequest(Chicago, snap, rng));
  const failed = results.filter((r) => !r.ok);

  assert.ok(failed.length >= 60 && failed.length <= 200, `failed ${failed.length}`);
  assert.ok(failed.every((r) => r.region === 'us-central1'));
});

test('haversine distance is sane', () => {
  const km = Sim.haversineKm(51.51, -0.13, 40.71, -74.0); // London -> New York
  assert.ok(km > 5500 && km < 5650, `got ${km}`);
});

test('traffic stats cover a rolling window and ignore failures for latency', () => {
  const { fleet, clock, config } = world();
  const traffic = new Sim.TrafficSimulator(config.cities, () => clock.now, mulberry32(3));
  const snap = fleet.refresh();

  traffic.generate(snap, 50);
  const stats = traffic.stats();
  assert.equal(stats.total, 50);
  assert.equal(stats.successRate, 1);
  assert.ok(stats.p50Ms > 0 && stats.p95Ms >= stats.p50Ms);
  assert.equal(stats.byRegion['us-central1'], 50);

  clock.advance(11); // everything ages out of the 10s window
  assert.equal(traffic.stats().total, 0);
});

// ---------- guard and incidents ----------

test('guard refuses actions that drop the fleet below the floor', () => {
  const { fleet, config } = world();
  const snap = fleet.refresh(); // 12 VMs, floor 34% => at least 5 must survive
  const guard = new Sim.ChaosGuard(config.chaos, () => 0);

  assert.equal(guard.tryAuthorize(snap, Sim.regionScope(snap, 'us-central1')), null); // 12 -> 6 (50%)
  assert.match(guard.tryAuthorize(snap, { kind: 'region', target: 'x', regionIds: ['x'], healthyVictims: 9 }), /25%/); // 12 -> 3
});

test('guard honours the kill switch and the action budget', () => {
  const { fleet } = world();
  const snap = fleet.refresh();
  const scope = Sim.instanceScope(snap, all(snap)[0].name);

  assert.ok(new Sim.ChaosGuard({ enabled: false, minGlobalHealthyPercent: 34, maxActionsPerMinute: 30 }, () => 0).tryAuthorize(snap, scope));

  let now = 0;
  const guard = new Sim.ChaosGuard({ enabled: true, minGlobalHealthyPercent: 34, maxActionsPerMinute: 2 }, () => now);
  assert.equal(guard.tryAuthorize(snap, scope), null);
  assert.equal(guard.tryAuthorize(snap, scope), null);
  assert.ok(guard.tryAuthorize(snap, scope));
  now += 61_000;
  assert.equal(guard.tryAuthorize(snap, scope), null);
});

test('scopes resolve only real targets', () => {
  const { fleet } = world();
  const snap = fleet.refresh();
  assert.equal(Sim.zoneScope(snap, 'us-central1-a').healthyVictims, 2);
  assert.equal(Sim.regionScope(snap, 'europe-west1').healthyVictims, 6);
  assert.equal(Sim.instanceScope(snap, 'web-not-a-vm'), null);
  assert.equal(Sim.zoneScope(snap, 'mars-1-a'), null);
});

test('an incident closes with a measured recovery time', () => {
  const { fleet, clock } = world();
  const tracker = new Sim.IncidentTracker(() => clock.now);

  const victim = inRegion(fleet.refresh(), 'us-central1')[0];
  fleet.killInstance(victim.name);
  tracker.start('instance', victim.name, ['us-central1']);

  tracker.observe(fleet.refresh());
  assert.equal(tracker.recent()[0].recoveredAt, null);

  for (let i = 0; i < 20; i++) {
    clock.advance(1);
    tracker.observe(fleet.refresh());
  }

  const incident = tracker.recent()[0];
  assert.notEqual(incident.recoveredAt, null);
  // 1.5s notice + 2s provisioning + 2s staging + 4s health check = ~9.5s, measured on a 1s tick.
  assert.ok(incident.recoverySeconds >= 8 && incident.recoverySeconds <= 12, `got ${incident.recoverySeconds}`);
});

test('an incident is not closed by a stale healthy snapshot', () => {
  const { fleet, clock } = world();
  const tracker = new Sim.IncidentTracker(() => clock.now);
  const healthy = fleet.refresh();

  tracker.start('instance', 'vm', ['us-central1']);
  tracker.observe(healthy); // snapshot from before the kill became visible

  assert.equal(tracker.recent()[0].recoveredAt, null);
});

// ---------- the backend the page talks to ----------

test('local backend mirrors the HTTP status codes of the real endpoints', async () => {
  const { config, clock } = world();
  const backend = new Sim.LocalBackend(config, { clock: () => clock.now, rng: mulberry32(5) });

  assert.equal((await backend.act('zone', { zone: 'us-central1-a' })).status, 409); // before the first frame

  const frame = backend.buildFrame();
  assert.equal((await backend.act('zone', {})).status, 400);
  assert.equal((await backend.act('instance', { name: 'web-nope' })).status, 404);
  assert.equal((await backend.act('bogus', { name: 'x' })).status, 404);

  const ok = await backend.act('instance', { name: all(frame.fleet)[0].name });
  assert.equal(ok.status, 200);
  assert.equal(ok.body.ok, true);

  const refused = await backend.act('region', { region: 'us-central1' }); // 12 -> 5 healthy: 41%, allowed
  assert.equal(refused.status, 200);
  backend.buildFrame();
  const floor = await backend.act('region', { region: 'europe-west1' }); // would leave far below 34%
  assert.equal(floor.status, 409);
  assert.match(floor.body.message, /floor is 34%/);
});

test('local backend frames have the shape the page renders', () => {
  const { config, clock } = world();
  const backend = new Sim.LocalBackend(config, { clock: () => clock.now, rng: mulberry32(5) });
  const frame = backend.buildFrame();

  assert.deepEqual(Object.keys(frame).sort(), ['fleet', 'incidents', 'requests', 'traffic']);
  assert.equal(frame.requests.length, 8);
  assert.deepEqual(Object.keys(frame.fleet.regions[0].zones[0].instances[0]).sort(), ['name', 'region', 'state', 'stateSince', 'zone']);
  assert.deepEqual(Object.keys(frame.traffic).sort(), ['byRegion', 'p50Ms', 'p95Ms', 'successRate', 'total']);
});

test('an injected failure shows up as an incident and then heals', async () => {
  const { config, clock } = world();
  const backend = new Sim.LocalBackend(config, { clock: () => clock.now, rng: mulberry32(5) });
  const frame = backend.buildFrame();

  await backend.act('instance', { name: all(frame.fleet)[0].name });
  assert.equal(backend.buildFrame().incidents.length, 1);
  for (let i = 0; i < 24; i++) { clock.advance(0.5); backend.buildFrame(); }
  assert.notEqual(backend.latest.incidents[0].recoveredAt, null);
});

// ---------- the browser config must match the server's appsettings.json ----------

test('sim-config.js matches appsettings.json', () => {
  const settings = JSON.parse(fs.readFileSync(path.join(wwwroot, '..', 'appsettings.json'), 'utf8'));
  const { Fleet, Simulation, Chaos } = settings;

  assert.equal(siteConfig.targetPerRegion, Fleet.TargetPerRegion);
  assert.deepEqual(
    siteConfig.regions,
    Fleet.Regions.map((r) => ({ id: r.Id, name: r.Name, lat: r.Lat, lon: r.Lon, zones: r.Zones })));
  assert.deepEqual(
    siteConfig.cities,
    Fleet.Cities.map((c) => ({ name: c.Name, lat: c.Lat, lon: c.Lon, weight: c.Weight })));
  assert.deepEqual(siteConfig.sim, {
    provisioningSeconds: Simulation.ProvisioningSeconds,
    stagingSeconds: Simulation.StagingSeconds,
    startingSeconds: Simulation.StartingSeconds,
    stoppingSeconds: Simulation.StoppingSeconds,
    reconcileDelaySeconds: Simulation.ReconcileDelaySeconds,
    zoneOutageSeconds: Simulation.ZoneOutageSeconds,
  });
  assert.deepEqual(siteConfig.chaos, {
    enabled: Chaos.Enabled,
    minGlobalHealthyPercent: Chaos.MinGlobalHealthyPercent,
    maxActionsPerMinute: Chaos.MaxActionsPerMinute,
  });
});
