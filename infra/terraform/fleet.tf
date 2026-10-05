data "google_compute_image" "debian" {
  family  = "debian-12"
  project = "debian-cloud"
}

locals {
  fleet_sa_email = "${var.fleet_service_account_id}@${var.project_id}.iam.gserviceaccount.com"
}

# One health check, used twice: the load balancer stops sending traffic to a VM that fails it,
# and the MIG's autohealing replaces a VM that keeps failing it.
resource "google_compute_health_check" "web" {
  name                = "chaos-web-hc"
  check_interval_sec  = 5
  timeout_sec         = 5
  healthy_threshold   = 2
  unhealthy_threshold = 3

  http_health_check {
    port         = 80
    request_path = "/healthz"
  }
}

# Templates are regional because they name a regional subnet.
resource "google_compute_region_instance_template" "web" {
  for_each = var.regions

  name_prefix  = "chaos-web-${each.key}-"
  region       = each.key
  machine_type = var.machine_type
  tags         = ["chaos-web"]
  labels       = { app = "chaos-map", role = "fleet" }

  disk {
    source_image = data.google_compute_image.debian.self_link
    boot         = true
    auto_delete  = true
    disk_size_gb = 10
    disk_type    = "pd-balanced"
  }

  # No access_config block = no external IP.
  network_interface {
    subnetwork = google_compute_subnetwork.fleet[each.key].id
  }

  service_account {
    email  = local.fleet_sa_email
    scopes = ["logging-write", "monitoring-write"]
  }

  shielded_instance_config {
    enable_secure_boot          = true
    enable_vtpm                 = true
    enable_integrity_monitoring = true
  }

  metadata = {
    enable-oslogin = "TRUE"
  }
  metadata_startup_script = file("${path.module}/startup.sh")

  lifecycle {
    create_before_destroy = true
  }
}

resource "google_compute_region_instance_group_manager" "web" {
  for_each = var.regions

  # Must match Fleet:Regions[].MigName in the app's appsettings.json.
  name               = "web-${each.key}"
  region             = each.key
  base_instance_name = "web"
  target_size        = var.target_size_per_region

  distribution_policy_zones = [for z in each.value.zones : "${each.key}-${z}"]

  # EVEN keeps zones balanced. During a real zone outage EVEN waits for the zone; ANY would place VMs in
  # the surviving zones. Worth experimenting with once the live provider works.
  distribution_policy_target_shape = "EVEN"

  version {
    instance_template = google_compute_region_instance_template.web[each.key].self_link
  }

  named_port {
    name = "http"
    port = 80
  }

  auto_healing_policies {
    health_check      = google_compute_health_check.web.id
    initial_delay_sec = 120 # covers apt-get install nginx at first boot
  }

  update_policy {
    type                         = "PROACTIVE"
    minimal_action               = "REPLACE"
    instance_redistribution_type = "PROACTIVE"
    max_surge_fixed              = 3
    max_unavailable_fixed        = 3
  }
}
