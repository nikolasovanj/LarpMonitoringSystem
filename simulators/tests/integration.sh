#!/usr/bin/env bash
# Integration tests: a real Mosquitto broker + real simulator processes.
#
#   tests/integration.sh                      run everything
#   tests/integration.sh status cmd_accept    run selected tests
#
# Env: SIM=path/to/sim   IT_PORT=18830   SKIP_IMAGE=1 (skip the Docker image test)
# Needs: docker, mosquitto-clients, jq, GNU date/timeout (Linux or WSL2)

SIMDIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
SIM=${SIM:-$SIMDIR/build/sim}
PORT=${IT_PORT:-18830}              # not 1883, so it can run next to your dev stack
BROKER=iot-it-broker
NET=iot-it-net
IMAGE=iot-it-sim
TMP=$(mktemp -d)
PASS=0; FAIL=0
SIM_PIDS=()

for c in docker mosquitto_sub mosquitto_pub jq timeout awk; do
  command -v "$c" >/dev/null || { echo "missing prerequisite: $c" >&2; exit 2; }
done
[ -x "$SIM" ] || { echo "simulator binary not found: $SIM (build it first)" >&2; exit 2; }
docker info >/dev/null 2>&1 || { echo "docker daemon not reachable" >&2; exit 2; }

# ---------------------------------------------------------------- helpers

ok()    { PASS=$((PASS+1)); echo "  PASS  $1"; }
bad()   { FAIL=$((FAIL+1)); echo "  FAIL  $1${2:+   ($2)}"; }
check() { local d=$1; shift; if "$@"; then ok "$d"; else bad "$d"; fi; }
eq()    { if [ "$2" = "$3" ]; then ok "$1"; else bad "$1" "got '$2', want '$3'"; fi; }
not()   { ! "$@"; }                       # `!` can't be passed through "$@", this can
jqt() {                                   # jqt INPUT JQ_ARGS...
  local data=$1; shift
  if [ -z "$data" ]; then echo "        no message received" >&2; return 1; fi
  jq -e "$@" <<<"$data" >/dev/null 2>&1 || { echo "        got: $data" >&2; return 1; }
}

now()   { date +%s.%N; }
under() { awk -v a="$1" -v b="$2" -v lim="$3" 'BEGIN { exit !((b - a) < lim) }'; }

msub()  { mosquitto_sub -h 127.0.0.1 -p "$PORT" "$@"; }
mpub()  { mosquitto_pub -h 127.0.0.1 -p "$PORT" "$@"; }
T()     { printf 'factory/it/line1/%s/%s' "$1" "$2"; }      # T device suffix
LOG()   { printf '%s/%s.log' "$TMP" "$1"; }

# Start a native simulator. Extra args are VAR=value overrides (SENSORS=..., etc).
# Must NOT be called inside $(...), or SIM_PID and SIM_PIDS are lost with the subshell.
start_sim() {
  local id=$1; shift
  env -i PATH="$PATH" DEVICE_ID="$id" SITE=it LINE=line1 \
      BROKER_HOST=127.0.0.1 BROKER_PORT="$PORT" PUBLISH_INTERVAL_MS=200 \
      "$@" "$SIM" >"$(LOG "$id")" 2>&1 &
  SIM_PID=$!
  SIM_PIDS+=("$SIM_PID")
}

stop_sims() {
  local p i alive
  for p in "${SIM_PIDS[@]}"; do kill -TERM "$p" 2>/dev/null; done
  for i in $(seq 30); do
    alive=0
    for p in "${SIM_PIDS[@]}"; do kill -0 "$p" 2>/dev/null && alive=1; done
    [ "$alive" = 0 ] && break
    sleep 0.1
  done
  for p in "${SIM_PIDS[@]}"; do kill -9 "$p" 2>/dev/null; wait "$p" 2>/dev/null; done
  SIM_PIDS=()
}

wait_broker() {                           # wait_broker TRIES (0.5 s apart)
  local i
  for ((i = 0; i < $1; i++)); do
    mpub -t it/ping -m x -q 1 >/dev/null 2>&1 && return 0
    sleep 0.5
  done
  return 1
}

start_broker() {
  docker rm -f "$BROKER" >/dev/null 2>&1
  docker network create "$NET" >/dev/null 2>&1
  printf 'listener 1883\nallow_anonymous true\n' >"$TMP/mosquitto.conf"
  chmod 644 "$TMP/mosquitto.conf"
  docker run -d --name "$BROKER" --network "$NET" -p "127.0.0.1:$PORT:1883" \
      -v "$TMP/mosquitto.conf:/mosquitto/config/mosquitto.conf:ro" \
      eclipse-mosquitto:2 >/dev/null || return 1
  wait_broker 30
}

# Status is retained, so polling with a late subscriber works.
wait_status() {                           # wait_status DEVICE VALUE SECONDS
  local i got
  for ((i = 0; i < $3 * 2; i++)); do
    got=$(msub -t "$(T "$1" status)" -C 1 -W 1 2>/dev/null)
    [ "$got" = "$2" ] && return 0
    sleep 0.5
  done
  return 1
}

wait_log() {                              # wait_log FILE TEXT SECONDS (fixed string)
  local i
  for ((i = 0; i < $3 * 10; i++)); do
    grep -qF -- "$2" "$1" && return 0
    sleep 0.1
  done
  return 1
}

capture() {                               # capture SECONDS TOPIC_FILTER FILE
  timeout $(( $1 + 5 )) mosquitto_sub -h 127.0.0.1 -p "$PORT" -t "$2" -v -W "$1" >"$3" 2>/dev/null
  return 0
}

# Publish a command and return the cmd/result reply ("" if none arrives in 5 s).
send_cmd() {                              # send_cmd DEVICE JSON
  local out="$TMP/reply.$RANDOM" sp
  msub -t "$(T "$1" cmd/result)" -C 1 -W 5 >"$out" 2>/dev/null &
  sp=$!
  sleep 0.7                               # let the subscriber attach before we publish
  mpub -q 1 -t "$(T "$1" cmd)" -m "$2"
  wait "$sp" 2>/dev/null
  cat "$out"; rm -f "$out"
}

cleanup() {
  stop_sims
  docker rm -f "$BROKER" "$IMAGE" >/dev/null 2>&1
  docker network rm "$NET" >/dev/null 2>&1
  rm -rf "$TMP"
}
trap cleanup EXIT
trap 'exit 130' INT TERM

# ---------------------------------------------------------------- tests
# Every test uses its own device ID: status is retained, so reusing an ID
# would let one test see the previous test's leftovers.

test_status() {
  start_sim t1-status
  check "status becomes online" wait_status t1-status online 10
  sleep 1
  eq "retained: a late subscriber still sees online" \
     "$(msub -t "$(T t1-status status)" -C 1 -W 3)" online
}

test_telemetry() {
  local dev=t2-telem s msg
  start_sim $dev SENSORS=temperature,vibration,pressure
  wait_status $dev online 10 || bad "device did not come online"
  for s in temperature vibration pressure; do
    msg=$(msub -t "$(T $dev $s/telemetry)" -C 1 -W 5)
    check "$s: valid JSON, right device/sensor, number value, fresh ts" \
      jqt "$msg" --arg d $dev --arg s $s \
         '.device==$d and .sensor==$s and (.value|type=="number")
          and (.unit|type=="string") and (((.ts - now)|fabs) < 30)'
  done
}

test_subset() {
  local dev=t3-subset f="$TMP/t3.cap" topics want
  start_sim $dev SENSORS=temperature,pressure
  wait_status $dev online 10 || bad "device did not come online"
  capture 3 "$(T $dev '#')" "$f"
  topics=$(awk '{print $1}' "$f" | sort -u)
  want=$(printf '%s\n' "$(T $dev status)" "$(T $dev pressure/telemetry)" \
                       "$(T $dev temperature/telemetry)" | sort)
  eq "only status + the two configured sensors publish" "$topics" "$want"
}

test_two_devices() {
  local a=t4-a b=t4-b f="$TMP/t4.cap" na nb
  start_sim $a SENSORS=temperature
  start_sim $b SENSORS=temperature
  wait_status $a online 10 || bad "$a did not come online"
  wait_status $b online 10 || bad "$b did not come online"
  capture 4 'factory/it/line1/+/temperature/telemetry' "$f"
  na=$(grep -c "/$a/" "$f"); nb=$(grep -c "/$b/" "$f")
  check "$a keeps publishing ($na msgs in 4 s, expect ~20)" [ "$na" -ge 10 ]
  check "$b keeps publishing ($nb msgs in 4 s, expect ~20)" [ "$nb" -ge 10 ]
  eq "$a connected exactly once (no takeover loop)" \
     "$(grep -c '^connect: Connection Accepted' "$(LOG $a)")" 1
  eq "$b connected exactly once (no takeover loop)" \
     "$(grep -c '^connect: Connection Accepted' "$(LOG $b)")" 1
}

test_clean_stop() {
  local dev=t5-stop t0 t1 rc
  start_sim $dev
  wait_status $dev online 10 || bad "device did not come online"
  t0=$(now); kill -TERM "$SIM_PID"; wait "$SIM_PID"; rc=$?; t1=$(now)
  eq "exit code 0 on SIGTERM" "$rc" 0
  check "exits in under 2 s" under "$t0" "$t1" 2
  # A clean DISCONNECT suppresses the Last Will, so "offline" must have come from the app.
  check "status flips to offline" wait_status $dev offline 5
  check "log shows the graceful path" grep -qF 'shutting down' "$(LOG $dev)"
}

test_last_will() {
  local dev=t6-will
  start_sim $dev
  wait_status $dev online 10 || bad "device did not come online"
  kill -9 "$SIM_PID"; wait "$SIM_PID" 2>/dev/null
  check "broker publishes offline after kill -9 (Last Will)" wait_status $dev offline 10
  check "...and the app never ran its shutdown path" not grep -qF 'shutting down' "$(LOG $dev)"
}

test_cmd_accept() {
  local dev=t7-cmd log reply; log=$(LOG $dev)
  start_sim $dev SENSORS=temperature,pressure
  wait_status $dev online 10 || bad "device did not come online"
  reply=$(send_cmd $dev '{"sensor":"pressure","fault":"ramp","duration":15}')
  check "reply is {\"ok\":true}" jqt "$reply"  '.ok==true'
  check "pressure enters the ramp fault" wait_log "$log" "$dev/pressure: fault none -> ramp" 5
  check "the fault ends after its duration" wait_log "$log" "$dev/pressure: fault ramp -> none" 10
  check "temperature was not affected" not grep -qF "$dev/temperature: fault" "$log"
}

test_cmd_reject() {
  local dev=t8-reject log c json frag reply; log=$(LOG $dev)
  local cases=(
    '{"sensor":"vibration","fault":"spike"}|no sensor'
    '{"sensor":"pressure","fault":"explode"}|unknown fault'
    '{"sensor":"pressure","fault":"spike","duration":0}|duration'
    '{"sensor":"pressure"}|required'
    'not json|JSON object'
    '{"sensor":"a\"b","fault":"spike"}|a"b'          # reply must stay valid JSON (escaping)
  )
  start_sim $dev SENSORS=temperature,pressure
  wait_status $dev online 10 || bad "device did not come online"
  for c in "${cases[@]}"; do
    json=${c%%|*}; frag=${c#*|}
    reply=$(send_cmd $dev "$json")
    check "rejected with a reason: $json" \
      jqt "$reply"  --arg f "$frag" '.ok==false and (.error|contains($f))'
  done
  check "no rejected command injected a fault" not grep -qF 'fault none ->' "$log"
}

test_retained_cmd() {
  local dev=t9-retained log; log=$(LOG $dev)
  # Retained BEFORE the device exists: the broker will replay it on every subscribe.
  mpub -r -q 1 -t "$(T $dev cmd)" -m '{"sensor":"pressure","fault":"stuck","duration":500}'
  start_sim $dev SENSORS=temperature,pressure
  wait_status $dev online 10 || bad "device did not come online"
  check "retained command is detected and ignored" wait_log "$log" 'ignoring retained command' 5
  sleep 2
  check "no fault was injected" not grep -qF 'fault none ->' "$log"
  mpub -r -n -t "$(T $dev cmd)"                        # clear the retained message
}

test_broker_restart() {
  local dev=t10-restart log msg reply; log=$(LOG $dev)
  start_sim $dev SENSORS=temperature,pressure
  wait_status $dev online 10 || bad "device did not come online"
  docker restart -t 1 "$BROKER" >/dev/null
  wait_broker 60 || { bad "broker did not come back"; return; }
  check "simulator process survived" kill -0 "$SIM_PID"
  check "status republished after reconnect" wait_status $dev online 40
  msg=$(msub -t "$(T $dev temperature/telemetry)" -C 1 -W 20)
  check "telemetry resumes" [ -n "$msg" ]
  check "reconnected (>= 2 CONNACKs in the log)" \
    [ "$(grep -c '^connect: Connection Accepted' "$log")" -ge 2 ]
  reply=$(send_cmd $dev '{"sensor":"pressure","fault":"spike"}')
  check "cmd subscription was restored" jqt "$reply" '.ok==true'
}

test_image_sigterm() {
  local dev=t11-image t0 t1
  if [ -n "$SKIP_IMAGE" ]; then echo "  skipped (SKIP_IMAGE set)"; return; fi
  docker build -q -t $IMAGE "$SIMDIR" >/dev/null || { bad "docker build failed"; return; }
  docker run -d --name $IMAGE --network "$NET" \
      -e DEVICE_ID=$dev -e SITE=it -e LINE=line1 -e BROKER_HOST="$BROKER" \
      -e PUBLISH_INTERVAL_MS=200 $IMAGE >/dev/null
  if ! wait_status $dev online 20; then
    bad "container did not come online (missing runtime lib? bad BROKER_HOST?)"
    docker logs --tail 15 $IMAGE 2>&1 | sed 's/^/        /'
    return
  fi
  ok "container starts and connects (runtime libraries present)"
  t0=$(now); docker stop $IMAGE >/dev/null; t1=$(now)
  # With no SIGTERM handler PID 1 ignores the signal and docker stop waits 10 s, then SIGKILLs.
  check "docker stop finishes in under 3 s (SIGTERM reaches the handler)" under "$t0" "$t1" 3
  eq "container exit code 0" "$(docker inspect -f '{{.State.ExitCode}}' $IMAGE)" 0
  check "status flips to offline" wait_status $dev offline 5
}

# ---------------------------------------------------------------- main

ALL=(status telemetry subset two_devices clean_stop last_will
     cmd_accept cmd_reject retained_cmd broker_restart image_sigterm)
SELECTED=("$@"); [ ${#SELECTED[@]} -eq 0 ] && SELECTED=("${ALL[@]}")

echo "broker: eclipse-mosquitto:2 on 127.0.0.1:$PORT    sim: $SIM"
start_broker || { echo "could not start the broker" >&2; exit 2; }

for t in "${SELECTED[@]}"; do
  declare -F "test_$t" >/dev/null || { echo "unknown test: $t (have: ${ALL[*]})" >&2; exit 2; }
  echo; echo "== $t"
  "test_$t"
  stop_sims
done

echo; echo "$PASS passed, $FAIL failed"
if [ "$FAIL" -ne 0 ]; then
  echo; echo "---- logs ----"
  for f in "$TMP"/*.log; do [ -f "$f" ] && { echo "## $(basename "$f")"; tail -n 15 "$f"; }; done
  echo "## broker"; docker logs --tail 20 "$BROKER" 2>&1
  exit 1
fi