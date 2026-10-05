# Sandbox infrastructure (project `gcechaosmap`)

Creates: a custom VPC, four regional subnets with Cloud NAT, firewall rules, one health check, a regional instance
template and **regional MIG per region** (`web-<region>`), and a global external Application Load Balancer.

Not managed here (created once by hand, see the setup notes): the `chaos-map-app` and `chaos-fleet-vm` service
accounts, the `chaosMapRunner` custom role, quotas, and the budget.

## Cost: read this before `apply`

Rough list-price estimates in USD, so check the pricing calculator before relying on them.
The default fleet is 12 e2-micro VMs, 4 Cloud NAT gateways and one global forwarding rule.

| Running | Roughly |
|---|---|
| Whole stack | 0.2 USD per hour, about 5 USD per day, about 150 USD per month |
| `target_size_per_region = 0` | Still about 0.07 USD per hour (load balancer and NAT bill even with no VMs) |

The budget is 25 CAD. **Do not leave this running.** Apply for a session, then destroy.

```bash
terraform init
terraform plan -out tfplan
terraform apply tfplan
terraform destroy
```

## Using it with the Chaos Map app

Set the provider through environment variables (no code change) and run the app with credentials that impersonate
`chaos-map-app`:

```
Fleet__Provider=Gce
Fleet__TargetPerRegion=3
Gce__ProjectId=gcechaosmap
```

`Fleet:Regions[].MigName` in `appsettings.json` already matches `web-<region>`.
