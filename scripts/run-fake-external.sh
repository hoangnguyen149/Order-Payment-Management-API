#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
[[ -f .env ]] || { echo 'Run bash scripts/init-dev-env.sh first.' >&2; exit 1; }
set -a
source .env
set +a
export Payments__WebhookSecret="$PAYMENT_WEBHOOK_SECRET"
export Fake__AdminKey="$SIMULATOR_ADMIN_KEY"
export Fake__DatabasePath="${Fake__DatabasePath:-$PWD/artifacts/fake-external.db}"
export Fake__ApiBaseUrl="${Fake__ApiBaseUrl:-http://localhost:8080}"
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS=http://localhost:8090
exec dotnet run --project src/OrderPayment.FakeExternalApi "$@"
