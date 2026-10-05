# Custom-mode VPC: nothing is created in regions we did not ask for.
resource "google_compute_network" "vpc" {
  name                    = "chaos-vpc"
  auto_create_subnetworks = false
}

resource "google_compute_subnetwork" "fleet" {
  for_each = var.regions

  name                     = "chaos-${each.key}"
  region                   = each.key
  network                  = google_compute_network.vpc.id
  ip_cidr_range            = each.value.cidr
  private_ip_google_access = true
}

# VMs have no external IP. Cloud NAT gives them outbound-only internet (apt-get at boot).
resource "google_compute_router" "nat" {
  for_each = var.regions

  name    = "chaos-router-${each.key}"
  region  = each.key
  network = google_compute_network.vpc.id
}

resource "google_compute_router_nat" "nat" {
  for_each = var.regions

  name                               = "chaos-nat-${each.key}"
  region                             = each.key
  router                             = google_compute_router.nat[each.key].name
  nat_ip_allocate_option             = "AUTO_ONLY"
  source_subnetwork_ip_ranges_to_nat = "ALL_SUBNETWORKS_ALL_IP_RANGES"
}

# The only inbound traffic the VMs accept: Google's load balancer and health-check ranges, on port 80.
resource "google_compute_firewall" "lb_and_health_checks" {
  name    = "chaos-allow-lb-health"
  network = google_compute_network.vpc.name

  direction     = "INGRESS"
  source_ranges = ["130.211.0.0/22", "35.191.0.0/16"]
  target_tags   = ["chaos-web"]

  allow {
    protocol = "tcp"
    ports    = ["80"]
  }
}

# SSH only through Identity-Aware Proxy (no public port 22).
resource "google_compute_firewall" "iap_ssh" {
  name    = "chaos-allow-iap-ssh"
  network = google_compute_network.vpc.name

  direction     = "INGRESS"
  source_ranges = ["35.235.240.0/20"]
  target_tags   = ["chaos-web"]

  allow {
    protocol = "tcp"
    ports    = ["22"]
  }
}
