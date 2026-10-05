output "load_balancer_ip" {
  description = "Open http://<this> in a browser once the VMs are healthy (allow a few minutes)."
  value       = google_compute_global_address.lb.address
}

output "mig_names" {
  description = "Regional MIG per region. These are what the Chaos Map app reads and deletes VMs from."
  value       = { for k, m in google_compute_region_instance_group_manager.web : k => m.name }
}

output "fleet_service_account" {
  value = local.fleet_sa_email
}
