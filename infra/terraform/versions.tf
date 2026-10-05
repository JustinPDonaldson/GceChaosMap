terraform {
  required_version = ">= 1.6"

  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
  }

  # Local state on purpose for the sandbox: one person, one project, easy to throw away.
  # Move to a GCS backend if more than one person ever runs this.
}

provider "google" {
  project = var.project_id
}
