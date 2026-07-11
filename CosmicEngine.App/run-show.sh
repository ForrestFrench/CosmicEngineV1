#!/bin/bash
# Cosmic Engine - Scene Dashboard Launcher (double-click friendly)
#
# Starts the engine in unbounded "show mode" at the Safe profile and opens
# the Scene Dashboard in your browser at http://localhost:8080. This is NOT
# a bounded diagnostic - it runs until you quit it, either via the
# dashboard's "Quit Engine" button or by closing this window / Ctrl+C.
#
# Usually launched via "Run Cosmic Engine.command" (double-click from
# Finder/Desktop) rather than run directly - see README_LAUNCHER.md.

set -u

# --- Resolve this script's real location, following symlinks/aliases so it
#     works correctly no matter where it's launched from (Desktop, Dock,
#     Finder alias) - never assumes the current working directory is right.
resolve_script_dir() {
  local source="${BASH_SOURCE[0]}"
  while [ -h "$source" ]; do
    local dir
    dir="$(cd -P "$(dirname "$source")" >/dev/null 2>&1 && pwd)"
    source="$(readlink "$source")"
    [[ "$source" != /* ]] && source="$dir/$source"
  done
  cd -P "$(dirname "$source")" >/dev/null 2>&1 && pwd
}

SCRIPT_DIR="$(resolve_script_dir)"
DASHBOARD_URL="http://localhost:8080"

# --- Cleanup trap (pre-commit correction) -----------------------------------
# ENGINE_PID is only ever set once WE start our own dotnet process (see
# Launch, below) - it stays empty through every early-exit path (dependency
# failures, duplicate-instance detection), so this cannot touch an
# already-running instance detected via /status, only a process this
# specific launcher invocation started itself.
ENGINE_PID=""

cleanup() {
  if [ -n "$ENGINE_PID" ] && kill -0 "$ENGINE_PID" 2>/dev/null; then
    kill "$ENGINE_PID" 2>/dev/null
    # Give it a moment to exit on its own (SIGTERM) before forcing it.
    for _ in 1 2 3 4 5 6 7 8 9 10; do
      kill -0 "$ENGINE_PID" 2>/dev/null || break
      sleep 0.2
    done
    kill -9 "$ENGINE_PID" 2>/dev/null
  fi
}

# On a normal exit (including after `wait "$ENGINE_PID"` returns because the
# engine already quit itself via the dashboard) cleanup() finds nothing alive
# and does/prints nothing - it's a silent no-op, not an error path. On
# INT/TERM/HUP, stop whatever we started, then exit explicitly (a signal
# trap that doesn't exit would otherwise let the script keep running past
# the interrupted `wait`).
trap cleanup EXIT
trap 'cleanup; exit 0' INT TERM HUP

fail() {
  echo ""
  echo "=========================================="
  echo "  Cosmic Engine could not start"
  echo "=========================================="
  echo "$1"
  echo ""
  echo "This window will stay open so you can read this message."
  read -r -p "Press Enter to close this window..." _ 2>/dev/null
  exit 1
}

cd "$SCRIPT_DIR" || fail "Could not switch to the project directory:
  $SCRIPT_DIR"

echo "Cosmic Engine - Scene Dashboard Launcher"
echo "Project directory: $SCRIPT_DIR"
echo ""

# --- Dependency checks ------------------------------------------------------
if ! command -v dotnet >/dev/null 2>&1; then
  fail "The 'dotnet' command was not found.
Install the .NET SDK from https://dotnet.microsoft.com/download and try again."
fi

if [ ! -f "$SCRIPT_DIR/CosmicEngine.App.csproj" ]; then
  fail "Expected to find CosmicEngine.App.csproj in:
  $SCRIPT_DIR
but it's missing. This launcher must stay inside the CosmicEngine.App
folder - if you copied it elsewhere (e.g. to your Desktop) instead of
making a Finder alias, move it back or re-create it as an alias.
See README_LAUNCHER.md."
fi

if ! command -v curl >/dev/null 2>&1; then
  fail "The 'curl' command was not found (unexpected on macOS).
Cannot check whether the dashboard is reachable without it."
fi

# --- Duplicate-instance check ------------------------------------------------
if curl -s -m 2 "$DASHBOARD_URL/status" >/dev/null 2>&1; then
  echo "Cosmic Engine already appears to be running."
  echo "Opening the existing dashboard instead of starting a second instance..."
  if command -v open >/dev/null 2>&1; then
    open "$DASHBOARD_URL" 2>/dev/null || echo "Could not auto-open browser. Dashboard: $DASHBOARD_URL"
  else
    echo "Dashboard: $DASHBOARD_URL"
  fi
  echo ""
  echo "If this isn't showing what you expect, quit the existing engine"
  echo "from its dashboard (the 'Quit Engine' button) and run this launcher"
  echo "again."
  read -r -p "Press Enter to close this window..." _ 2>/dev/null
  exit 0
fi

# --- Launch -------------------------------------------------------------
mkdir -p "$SCRIPT_DIR/DiagnosticReports" 2>/dev/null
LOG_FILE="$SCRIPT_DIR/DiagnosticReports/launcher_last_run.log"

echo "Starting Cosmic Engine (Safe profile)..."
dotnet run -- --profile Safe > "$LOG_FILE" 2>&1 &
ENGINE_PID=$!

echo "Waiting for the dashboard to start..."
DASHBOARD_UP=0
for _ in $(seq 1 60); do
  if ! kill -0 "$ENGINE_PID" 2>/dev/null; then
    break
  fi
  if curl -s -m 1 "$DASHBOARD_URL/status" >/dev/null 2>&1; then
    DASHBOARD_UP=1
    break
  fi
  sleep 0.5
done

if [ "$DASHBOARD_UP" -eq 1 ]; then
  if command -v open >/dev/null 2>&1; then
    open "$DASHBOARD_URL" 2>/dev/null || echo "Could not auto-open browser. Dashboard: $DASHBOARD_URL"
  else
    echo "Dashboard: $DASHBOARD_URL"
  fi
  echo ""
  echo "Dashboard: $DASHBOARD_URL"
  echo "Stellar Nursery uses the known-good show seed (777) by default."
  echo "Quit the engine anytime from the dashboard's 'Quit Engine' button,"
  echo "or close this window / press Ctrl+C."
  echo ""
  wait "$ENGINE_PID"
  echo ""
  echo "Cosmic Engine has exited."
else
  echo ""
  echo "Cosmic Engine did not come up within the expected time."
  echo "Last output from the engine (full log: $LOG_FILE):"
  echo "----------------------------------------"
  tail -n 40 "$LOG_FILE" 2>/dev/null
  echo "----------------------------------------"
  read -r -p "Press Enter to close this window..." _ 2>/dev/null
  exit 1
fi
