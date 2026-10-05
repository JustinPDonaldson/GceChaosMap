variable "project_id" {
  description = "The dedicated sandbox project. Nothing in this module should ever point at another project."
  type        = string
  default     = "gcechaosmap"

  validation {
    condition     = var.project_id == "gcechaosmap"
    error_message = "This module is hard-wired to the gcechaosmap sandbox. Edit the validation deliberately if you really mean another project."
  }
}

variable "regions" {
  description = "Regions to run a regional MIG in. Keys must match Fleet:Regions[].Id in appsettings.json."
  type = map(object({
    name  = string
    zones = list(string)
    cidr  = string
  }))
  default = {
    "us-central1"        = { name = "Iowa", zones = ["a", "b", "c"], cidr = "10.10.1.0/24" }
    "europe-west1"       = { name = "Belgium", zones = ["b", "c", "d"], cidr = "10.10.2.0/24" }
    "asia-southeast1"    = { name = "Singapore", zones = ["a", "b", "c"], cidr = "10.10.3.0/24" }
    "southamerica-east1" = { name = "Sao Paulo", zones = ["a", "b", "c"], cidr = "10.10.4.0/24" }
  }
}

variable "target_size_per_region" {
  description = "VMs per regional MIG. 3 puts one VM in each zone. 0 turns the fleet off (the load balancer and NAT still bill)."
  type        = number
  default     = 3

  validation {
    condition     = var.target_size_per_region >= 0 && var.target_size_per_region <= 6
    error_message = "Keep this between 0 and 6. The regional CPU quota is 12."
  }
}

variable "machine_type" {
  type    = string
  default = "e2-micro"
}

variable "fleet_service_account_id" {
  description = "Account id (not email) of the service account the fleet VMs run as. Created outside Terraform with only logging and monitoring roles."
  type        = string
  default     = "chaos-fleet-vm"
}
