# GCE Chaos Map

An interactive world map of a multi-region Compute Engine fleet. Visitors delete a VM, fail a zone, or take out a whole region and watch:

- the regional **managed instance group** notice, recreate and health-check replacements (VM states are drawn live),
- a **global load balancer** route synthetic users to the nearest healthy region, with failover traffic and latency shown on the map,
- a measured **recovery time** for every incident.

ASP.NET Core (.NET 10) + SignalR on the back end, plain JS + D3 + canvas on the front end. No build step.

## Run it

```bash
dotnet run --project src/ChaosMap.Api --urls http://localhost:5116
dotnet test
```

Open <http://localhost:5116>. The default `Simulation` provider needs no cloud account and costs nothing.

## Architecture

```
browser  <-- SignalR "state" frame every 500ms --  FleetLoop
   |                                                 |-- IFleetProvider.RefreshAsync  (fleet truth)
   |  POST /api/chaos/{instance|zone|region|reset}   |-- TrafficSimulator             (synthetic users -> LB routing)
   +---------------------------------------------->  |-- IncidentTracker              (recovery timing)
                                                     `-- ChaosGuard + rate limiter    (safety)
```

| Piece | Job |
|---|---|
| `IFleetProvider` | The only thing that can read or break the fleet. |
| `SimulatedFleet` | Models regional MIGs: autohealing delay, least-loaded-zone placement, PROVISIONING → STAGING → health check, zone outage window. Stepped in 250 ms slices so results don't depend on poll rate. |
| `GceFleetProvider` | Real Compute Engine: lists each regional MIG's managed instances, maps `currentAction` / `instanceStatus` / health to the map's states, deletes VMs via `InstancesClient`. The MIG's own autohealing does the recovering; this class never recreates anything. |
| `TrafficSimulator` | Routes each request to the nearest region with a healthy backend. Requests landing on a VM that is mid-delete fail (the balancer hasn't noticed yet), which is what makes a kill show up as an error blip. |
| `ChaosGuard` | Kill switch, global actions-per-minute budget, and a floor on how much of the fleet may be down (default: 34% must stay healthy). |
| Rate limiter | Per-visitor token bucket on `/api/chaos/*`. |

## GCE mode (not yet exercised against a real project)

`GceFleetProvider` compiles and its state mapping is unit-tested, but it has **not been run against real Compute Engine yet**. To try it you need, per region in `Fleet:Regions`, a regional MIG named `MigName` (ideally with an autohealing health check so `Starting` vs `Healthy` is meaningful), and:

```jsonc
"Fleet": { "Provider": "Gce", "TargetPerRegion": 3 },
"Gce":   { "ProjectId": "<a dedicated sandbox project>" }
```

Use a **dedicated project** and a service account limited to `compute.instanceGroupManagers.get/list` and `compute.instances.delete`. The API only deletes instances that appear in a configured MIG, but anyone who can reach the site can trigger deletes, so keep `Chaos:MaxActionsPerMinute` and `MinGlobalHealthyPercent` conservative.

Known differences from simulation:

- "Fail zone" / "Fail region" delete the VMs in that zone/region; there is no real zone outage, so the MIG may recreate VMs in the same zone. Draining a zone from the MIG's distribution policy is the next step.
- The synthetic users are still simulated; only fleet state is real.

## Known limitations

- Incident recovery is measured per region ("region whole again"), so overlapping incidents in one region finish together.
- On phone widths the region cards are small; the panels below the map are the readable part.
- Map libraries and land outlines load from jsDelivr. Vendor them into `wwwroot/lib` for offline or locked-down hosting.

## Next steps

1. Terraform for the sandbox project: VPC, instance template, four regional MIGs with autohealing, global external Application Load Balancer, least-privilege service account.
2. Run `GceFleetProvider` against it and fix whatever real Compute Engine disagrees with.
3. Real zone drain via the MIG distribution policy.
4. Deploy the app (Cloud Run or a small VM behind the same LB), budget alert, and a "replay mode" fallback when the live fleet is off.
