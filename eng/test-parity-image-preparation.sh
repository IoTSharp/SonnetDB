#!/usr/bin/env bash
# Exercise orchestration/ownership without building or starting any containers.
set -euo pipefail
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
fixture_root="$(mktemp -d "${TMPDIR:-/tmp}/sonnetdb-parity-image-test.XXXXXX")"
fixture_root="$(cd -- "$fixture_root" && pwd -P)"
test_pid=""
test_deadline=$((SECONDS + 60))
cleanup() {
  local result=$?
  trap - EXIT INT TERM
  if [[ -n "$test_pid" ]]; then kill -TERM "$test_pid" 2>/dev/null || true; wait "$test_pid" || true; fi
  # mktemp created this exact private root; it contains only these fixtures.
  if [[ "$fixture_root" == */sonnetdb-parity-image-test.?????? ]]; then rm -rf -- "$fixture_root"; else result=1; fi
  exit "$result"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
mkdir "$fixture_root/bin"
cat > "$fixture_root/bin/docker" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
state="$PARITY_TEST_STATE"
scenario="$PARITY_TEST_SCENARIO"
case "$1 $2" in
  'system df') echo 'Images: fixture only';;
  'buildx inspect')
    printf 'inspect:%s\n' "$3" >> "$state/events"
    [[ -f "$state/$3" ]];;
  'buildx create')
    name="$4"
    printf 'create:%s\n' "$name" >> "$state/events"
    if [[ "$scenario" == create-failure ]]; then exit 19; fi
    [[ "$*" == *'--driver docker-container'* && "$*" == *'cpu-quota=200000'* ]]
    config="${@: -1}"
    [[ "$(< "$config")" == $'[worker.oci]\n  max-parallelism = 2' ]]
    : > "$state/$name";;
  'buildx bake')
    target="${@: -1}"
    printf 'bake:%s\n' "$target" >> "$state/events"
    [[ "$*" == *'--file docker-compose.parity.yml --load '* ]]
    if [[ "$scenario" == build-failure ]]; then exit 42; fi
    if [[ "$scenario" == cancel ]]; then : > "$state/baking"; sleep 20; fi;;
  'buildx rm')
    name="${@: -1}"
    printf 'remove:%s\n' "$name" >> "$state/events"
    [[ "$3" == --force && -f "$state/$name" ]]
    if [[ "$scenario" == cleanup-failure ]]; then exit 32; fi
    rm -- "$state/$name";;
  'compose -f')
    [[ "$3" == docker-compose.parity.yml && "$6 $7" == 'config --images' ]]
    if [[ "$8" == sonnetdb ]]; then echo iotsharp/sonnetdb:parity; else echo sonnetdb-parity-minio:RELEASE.2024-09-22T00-33-43Z; fi;;
  'image inspect')
    printf 'loaded:%s\n' "${@: -1}" >> "$state/events"
    if [[ "$scenario" == missing-image ]]; then exit 1; fi
    echo sha256:fixture;;
  *) echo "Unexpected Docker operation: $*" >&2; exit 90;;
esac
EOF
chmod +x "$fixture_root/bin/docker"

assert() {
  if (( SECONDS >= test_deadline )) || ! "$@"; then
    echo "FAIL: $*" >&2
    if [[ -f "${state:-}/output" ]]; then head -c 8192 "$state/output" >&2; fi
    exit 1
  fi
}
case_number=0
prepare_case() {
  case_number=$((case_number + 1))
  state="$fixture_root/case-$case_number"
  mkdir -p "$state/runner-temp"
  : > "$state/events"
}
run_case() {
  local scenario="$1" profile="$2"
  result=0
  env PATH="$fixture_root/bin:$PATH" PARITY_TEST_STATE="$state" PARITY_TEST_SCENARIO="$scenario" \
    RUNNER_TEMP="$state/runner-temp" GITHUB_RUN_ID=12345 GITHUB_RUN_ATTEMPT=2 \
    timeout --signal=TERM --kill-after=3s 12s bash "$repo_root/eng/prepare-parity-images.sh" "$profile" "$state/diagnostics" \
    > "$state/output" 2>&1 || result=$?
  local scratch=("$state"/runner-temp/sonnetdb-parity-build-*)
  assert test ! -e "${scratch[0]}"
}

# Both profiles must build/load/clean in order, leaving foreign state intact.
for profile in light full; do
  prepare_case
  : > "$state/foreign-builder"
  run_case success "$profile"
  assert test "$result" -eq 0
  expected="inspect:sonnetdb-parity-12345-2-$profile-sonnetdb
create:sonnetdb-parity-12345-2-$profile-sonnetdb
bake:sonnetdb
loaded:iotsharp/sonnetdb:parity
remove:sonnetdb-parity-12345-2-$profile-sonnetdb
inspect:sonnetdb-parity-12345-2-$profile-minio
create:sonnetdb-parity-12345-2-$profile-minio
bake:minio
loaded:sonnetdb-parity-minio:RELEASE.2024-09-22T00-33-43Z
remove:sonnetdb-parity-12345-2-$profile-minio"
  assert test "$(< "$state/events")" = "$expected"
  assert test -f "$state/foreign-builder"
done

prepare_case
: > "$state/sonnetdb-parity-12345-2-full-sonnetdb"
run_case success full
assert test "$result" -ne 0
assert test "$(< "$state/events")" = 'inspect:sonnetdb-parity-12345-2-full-sonnetdb'
assert test -f "$state/sonnetdb-parity-12345-2-full-sonnetdb"

prepare_case
run_case create-failure full
assert test "$result" -eq 19
assert test "$(< "$state/events")" = $'inspect:sonnetdb-parity-12345-2-full-sonnetdb\ncreate:sonnetdb-parity-12345-2-full-sonnetdb'

for scenario in build-failure missing-image cleanup-failure; do
  prepare_case
  run_case "$scenario" full
  assert test "$result" -ne 0
  events="$(< "$state/events")"
  assert test "${events##*$'\n'}" = 'remove:sonnetdb-parity-12345-2-full-sonnetdb'
  assert test "${events#*bake:minio}" = "$events"
  if [[ "$scenario" == cleanup-failure ]]; then
    assert test -f "$state/sonnetdb-parity-12345-2-full-sonnetdb"
  else
    assert test ! -e "$state/sonnetdb-parity-12345-2-full-sonnetdb"
  fi
done

prepare_case
env PATH="$fixture_root/bin:$PATH" PARITY_TEST_STATE="$state" PARITY_TEST_SCENARIO=cancel \
  RUNNER_TEMP="$state/runner-temp" GITHUB_RUN_ID=12345 GITHUB_RUN_ATTEMPT=2 \
  bash "$repo_root/eng/prepare-parity-images.sh" full "$state/diagnostics" > "$state/output" 2>&1 &
test_pid=$!
cancel_deadline=$((SECONDS + 5))
for (( attempt=0; attempt<60 && SECONDS<cancel_deadline; attempt++ )); do
  if [[ -f "$state/baking" ]]; then break; fi
  sleep 0.05
done
assert test -f "$state/baking"
kill -TERM "$test_pid"
result=0
wait "$test_pid" || result=$?
test_pid=""
assert test "$result" -eq 143
assert test ! -e "$state/sonnetdb-parity-12345-2-full-sonnetdb"
assert test "$(tail -n 1 "$state/events")" = 'remove:sonnetdb-parity-12345-2-full-sonnetdb'
scratch=("$state"/runner-temp/sonnetdb-parity-build-*)
assert test ! -e "${scratch[0]}"

workflow="$(< "$repo_root/.github/workflows/parity.yml")"
assert test "${workflow#*services=(sonnetdb postgres redis minio nats)}" != "$workflow"
assert test "${workflow#*services+=(influxdb victoriametrics mosquitto meilisearch qdrant clickhouse mongodb)}" != "$workflow"
assert test "${workflow#*up -d --no-build}" != "$workflow"
before_start="${workflow%%up -d --no-build*}"
assert test "${before_start#*bash ../../eng/prepare-parity-images.sh}" != "$before_start"
echo "Parity image preparation contracts passed: $case_number scenarios (including both profiles and cancellation)."
