#!/bin/bash
# Runs as root on every boot. Installs nginx and serves a page that says which VM answered,
# plus /healthz for the load balancer and the MIG's autohealing.
set -euo pipefail

if ! command -v nginx >/dev/null 2>&1; then
  apt-get update -y
  DEBIAN_FRONTEND=noninteractive apt-get install -y nginx
fi

meta() { curl -sf -H "Metadata-Flavor: Google" "http://metadata.google.internal/computeMetadata/v1/$1"; }

NAME="$(meta instance/name)"
ZONE="$(meta instance/zone | awk -F/ '{print $NF}')"

cat > /var/www/html/index.html <<EOF
<!doctype html>
<title>${NAME}</title>
<h1>Served by ${NAME}</h1>
<p>Zone: ${ZONE}</p>
EOF

echo ok > /var/www/html/healthz
systemctl enable --now nginx
