#!/usr/bin/env bash
# Build/load local parity images before pulling the complete reference stack.
set -euo pipefail

if [[ $# -ne 2 || ! "$1" =~ ^(light|full)$ ]]; then
  echo "Usage: bash eng/prepare-parity-images.sh <light|full> <diagnostics-directory>" >&2
  exit 2
fi
profile="$1"
diagnostics="$2"
if [[ ! "${GITHUB_RUN_ID:-}" =~ ^[0-9]+$ || ! "${GITHUB_RUN_ATTEMPT:-}" =~ ^[0-9]+$ ]]; then
  echo "A numeric GitHub run ID and attempt are required for builder ownership." >&2
  exit 2
fi

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
cd "$repo_root/tests/SonnetDB.Parity"
compose_file=docker-compose.parity.yml
mkdir -p -- "$diagnostics"
diagnostics="$(cd -- "$diagnostics" && pwd -P)"
scratch_root="$(cd -- "${RUNNER_TEMP:-${TMPDIR:-/tmp}}" && pwd -P)"
scratch_dir=""
owned_builder=""
cleanup_attempted=0
active_pid=""
# Exactly two builds; all preparation commands share this 20-minute deadline.
deadline=$((SECONDS + 1200))

run_bounded() {
  local limit="$1"
  shift
  local remaining=$((deadline - SECONDS))
  if (( remaining <= 0 )); then
    echo "::error::Parity image preparation exceeded its 20-minute deadline." >&2
    return 124
  fi
  if (( remaining < limit )); then limit="$remaining"; fi
  timeout --signal=TERM --kill-after=10s "${limit}s" "$@" &
  active_pid=$!
  {
    printf 'pid=%s parent=%s started=%(%Y-%m-%dT%H:%M:%SZ)T timeout=%ss command=' "$active_pid" "$$" -1 "$limit"
    printf '%q ' "$@"
    printf '\n'
  } >> "$diagnostics/image-preparation-processes.txt"
  local result=0
  wait "$active_pid" || result=$?
  active_pid=""
  return "$result"
}

capture_capacity() {
  {
    printf '\nphase=%s time=%(%Y-%m-%dT%H:%M:%SZ)T\n' "$1" -1
    run_bounded 10 df -h "$repo_root" "$scratch_root" || true
    run_bounded 15 docker system df || true
  } >> "$diagnostics/image-preparation-capacity.txt" 2>&1
}

cleanup_builder() {
  if [[ -z "$owned_builder" || "$cleanup_attempted" -eq 1 ]]; then return 0; fi
  cleanup_attempted=1
  # This exact builder was absent before a successful create in this process.
  # No --keep-state: rm also removes only this builder's build cache/volume.
  # Cleanup has its own bounded allowance after a build timeout/cancellation.
  timeout --signal=TERM --kill-after=10s 60s docker buildx rm --force "$owned_builder" &
  active_pid=$!
  printf 'cleanup pid=%s parent=%s started=%(%Y-%m-%dT%H:%M:%SZ)T builder=%s\n' \
    "$active_pid" "$$" -1 "$owned_builder" >> "$diagnostics/image-preparation-processes.txt"
  local result=0
  wait "$active_pid" || result=$?
  active_pid=""
  if (( result != 0 )); then
    echo "::error::Could not remove owned builder $owned_builder (exit $result); refusing to start reference pulls." >&2
    return "$result"
  fi
  owned_builder=""
}

cleanup() {
  local result=$?
  trap - EXIT INT TERM
  cleanup_builder || { if (( result == 0 )); then result=1; fi; }
  if [[ -n "$scratch_dir" ]]; then
    # Only the two files in this freshly created private directory are removed.
    rm -f -- "$scratch_dir/buildkitd.toml" "$scratch_dir/image.txt" || result=1
    rmdir -- "$scratch_dir" || result=1
  fi
  exit "$result"
}

cancel() {
  trap - INT TERM
  if [[ -n "$active_pid" ]]; then
    # The unreaped direct child is our timeout wrapper, which forwards TERM to
    # its owned command group and applies its ten-second kill allowance.
    kill -TERM "$active_pid" 2>/dev/null || true
    wait "$active_pid" || true
    active_pid=""
  fi
  exit "$1"
}
trap cleanup EXIT
trap 'cancel 130' INT
trap 'cancel 143' TERM

scratch_dir="$(mktemp -d "$scratch_root/sonnetdb-parity-build-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}-${profile}.XXXXXX")"
cat > "$scratch_dir/buildkitd.toml" <<'EOF'
[worker.oci]
  max-parallelism = 2
EOF

for service in sonnetdb minio; do
  builder="sonnetdb-parity-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}-${profile}-${service}"
  if run_bounded 30 docker buildx inspect "$builder" >/dev/null 2>&1; then
    echo "::error::Builder $builder already exists; refusing to claim or remove it." >&2
    exit 1
  fi
  capture_capacity "before-$service-build"
  create_result=0
  run_bounded 60 docker buildx create --name "$builder" --driver docker-container \
    --driver-opt cpu-period=100000 --driver-opt cpu-quota=200000 \
    --driver-opt restart-policy=no --buildkitd-config "$scratch_dir/buildkitd.toml" || create_result=$?
  if (( create_result != 0 )); then
    echo "::error::Builder $builder creation did not succeed (exit $create_result); ownership is unconfirmed and it will not be removed." >&2
    exit "$create_result"
  fi
  owned_builder="$builder"
  cleanup_attempted=0

  # Bake reads the original Compose contexts, Dockerfiles and image tags. One
  # target and --load keep the final image in Docker after its builder is gone.
  if ! run_bounded 900 docker buildx bake --builder "$builder" --file "$compose_file" --load "$service"; then
    capture_capacity "$service-build-failed"
    echo "::error::Parity $service image build/load failed before reference pulls; see capacity diagnostics." >&2
    exit 1
  fi
  run_bounded 30 docker compose -f "$compose_file" --profile "$profile" config --images "$service" > "$scratch_dir/image.txt"
  mapfile -t image_tags < "$scratch_dir/image.txt"
  if [[ ${#image_tags[@]} -ne 1 || -z "${image_tags[0]}" ]]; then
    echo "::error::Expected one Compose image tag for $service." >&2
    exit 1
  fi
  run_bounded 30 docker image inspect --format '{{.Id}}' "${image_tags[0]}"
  capture_capacity "after-$service-load"
  cleanup_builder
  capture_capacity "after-$service-builder-cleanup"
done
