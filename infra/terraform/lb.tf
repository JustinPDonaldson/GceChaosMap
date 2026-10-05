# Global external Application Load Balancer: one anycast IP, requests go to the nearest region with healthy capacity.
resource "google_compute_global_address" "lb" {
  name = "chaos-lb-ip"
}

resource "google_compute_backend_service" "web" {
  name                  = "chaos-web-backend"
  protocol              = "HTTP"
  port_name             = "http"
  load_balancing_scheme = "EXTERNAL_MANAGED"
  timeout_sec           = 10
  health_checks         = [google_compute_health_check.web.id]

  connection_draining_timeout_sec = 10

  dynamic "backend" {
    for_each = google_compute_region_instance_group_manager.web
    content {
      group           = backend.value.instance_group
      balancing_mode  = "UTILIZATION"
      max_utilization = 0.8
      capacity_scaler = 1.0
    }
  }
}

resource "google_compute_url_map" "web" {
  name            = "chaos-web-map"
  default_service = google_compute_backend_service.web.id
}

resource "google_compute_target_http_proxy" "web" {
  name    = "chaos-web-proxy"
  url_map = google_compute_url_map.web.id
}

# Plain HTTP on purpose: this is a sandbox with no domain or certificate.
resource "google_compute_global_forwarding_rule" "web" {
  name                  = "chaos-web-fr"
  load_balancing_scheme = "EXTERNAL_MANAGED"
  ip_address            = google_compute_global_address.lb.id
  port_range            = "80"
  target                = google_compute_target_http_proxy.web.id
}
