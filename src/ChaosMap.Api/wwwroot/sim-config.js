/*
 * Regions, cities and timings for the in-browser simulation (static hosting has no appsettings.json).
 * Mirrors the Fleet, Simulation and Chaos sections of src/ChaosMap.Api/appsettings.json;
 * tests/js/sim.test.js fails if the two drift apart.
 */
(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.CHAOS_SIM_CONFIG = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  'use strict';

  return {
    targetPerRegion: 6,
    regions: [
      { id: 'us-central1', name: 'Iowa', lat: 41.26, lon: -95.86, zones: ['a', 'b', 'c'] },
      { id: 'europe-west1', name: 'Belgium', lat: 50.45, lon: 3.82, zones: ['b', 'c', 'd'] },
      { id: 'asia-southeast1', name: 'Singapore', lat: 1.35, lon: 103.82, zones: ['a', 'b', 'c'] },
      { id: 'southamerica-east1', name: 'São Paulo', lat: -23.55, lon: -46.63, zones: ['a', 'b', 'c'] },
    ],
    cities: [
      { name: 'San Francisco', lat: 37.77, lon: -122.42, weight: 3 },
      { name: 'New York', lat: 40.71, lon: -74.0, weight: 4 },
      { name: 'Mexico City', lat: 19.43, lon: -99.13, weight: 1.5 },
      { name: 'Bogotá', lat: 4.71, lon: -74.07, weight: 1 },
      { name: 'Buenos Aires', lat: -34.6, lon: -58.38, weight: 1.5 },
      { name: 'London', lat: 51.51, lon: -0.13, weight: 4 },
      { name: 'Frankfurt', lat: 50.11, lon: 8.68, weight: 3 },
      { name: 'Lagos', lat: 6.52, lon: 3.38, weight: 1.5 },
      { name: 'Johannesburg', lat: -26.2, lon: 28.05, weight: 1 },
      { name: 'Dubai', lat: 25.2, lon: 55.27, weight: 1.5 },
      { name: 'Mumbai', lat: 19.08, lon: 72.88, weight: 3 },
      { name: 'Jakarta', lat: -6.21, lon: 106.85, weight: 1.5 },
      { name: 'Tokyo', lat: 35.68, lon: 139.69, weight: 3 },
      { name: 'Sydney', lat: -33.87, lon: 151.21, weight: 2 },
    ],
    sim: {
      provisioningSeconds: 2,
      stagingSeconds: 2,
      startingSeconds: 4,
      stoppingSeconds: 2.5,
      reconcileDelaySeconds: 1.5,
      zoneOutageSeconds: 30,
    },
    chaos: {
      enabled: true,
      minGlobalHealthyPercent: 34,
      maxActionsPerMinute: 30,
    },
  };
});
