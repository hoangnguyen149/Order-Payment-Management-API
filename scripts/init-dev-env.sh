#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
command -v openssl >/dev/null || { echo 'Install openssl first.' >&2; exit 1; }
umask 077
if [[ -e .env ]]; then
  # Add requirements introduced after Phase 1; retain every existing setting/value.
  for task_key in PAYMENT_WEBHOOK_SECRET SIMULATOR_ADMIN_KEY; do
    if ! grep -q "^${task_key}=" .env; then
      printf '\n%s=%s\n' "$task_key" "$(openssl rand -hex 32)" >> .env
      echo "Added missing local setting: $task_key"
    fi
  done
  chmod 600 .env
  echo 'Existing .env values preserved. Keep the file private.'
  exit 0
fi
task_env_file=$(mktemp .env.tmp.XXXXXX)
trap 'rm -f "$task_env_file"' EXIT
{
  printf 'MSSQL_SA_PASSWORD=Aa1!%s\n' "$(openssl rand -hex 20)"
  printf 'JWT_SIGNING_KEY=%s\n' "$(openssl rand -hex 32)"
  printf 'ADMIN_PASSWORD=Aa1!%s\n' "$(openssl rand -hex 20)"
  printf 'STAFF_PASSWORD=Aa1!%s\n' "$(openssl rand -hex 20)"
  printf 'PAYMENT_WEBHOOK_SECRET=%s\n' "$(openssl rand -hex 32)"
  printf 'SIMULATOR_ADMIN_KEY=%s\n' "$(openssl rand -hex 32)"
} > "$task_env_file"
# Atomic creation: never overwrite another process/user's existing configuration.
ln "$task_env_file" .env
echo 'Created .env with random development credentials (mode 600). Keep it private.'
