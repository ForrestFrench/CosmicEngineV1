#!/bin/bash
# Cosmic Engine - Scene Dashboard Launcher (double-click friendly)
#
# Starts ONLY the Scene Dashboard (--dashboard-only) and opens it in your
# browser at http://localhost:8080 - no visual/scene window opens until you
# pick one from the dashboard. This is NOT a bounded diagnostic - it runs
# until you quit it, either via the dashboard's "Quit Engine" button or by
# closing this window / Ctrl+C.
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
but it's missing. This launcher must stay inside the CosmicEngineApp
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

# --- Build, then launch via the DLL directly (not `dotnet run`, not the ---
# generated apphost) -----------------------------------------------------
# AUDIT.md Entry 37 (Mac AMFI Apphost Workaround Pass): the locally-built
# apphost binary (bin/Debug/net8.0/CosmicEngine.App) is ad-hoc-signed, and
# on macOS Tahoe 26.5.2 that gets killed by AMFI/Gatekeeper at exec() time
# before any Cosmic Engine code runs (see Entries 35/36 for the original
# investigation) - `dotnet run` was hitting this because it execs that same
# apphost under the hood. The fix used here is two-pronged: (1) the .csproj
# now sets <UseAppHost>false</UseAppHost>, so no ad-hoc-signed apphost is
# generated at all, and (2) this script builds once, then launches the
# built .dll directly through the `dotnet` command itself - `dotnet` is
# Apple-notarized/Microsoft-signed, not a local ad-hoc build, so it is never
# subject to the same AMFI rejection. Keep launching this way (`dotnet
# <path-to-dll>`, not the bare apphost path) even if UseAppHost is ever
# reverted, since it's the part that actually avoids the rejected binary.
mkdir -p "$SCRIPT_DIR/DiagnosticReports" 2>/dev/null
LOG_FILE="$SCRIPT_DIR/DiagnosticReports/launcher_last_run.log"
BUILD_LOG="$SCRIPT_DIR/DiagnosticReports/launcher_build_last_run.log"
APP_DLL="$SCRIPT_DIR/bin/Debug/net8.0/CosmicEngine.App.dll"

echo "Building Cosmic Engine..."
if ! dotnet build > "$BUILD_LOG" 2>&1; then
  fail "The build failed. Last output (full log: $BUILD_LOG):
----------------------------------------
$(tail -n 40 "$BUILD_LOG" 2>/dev/null)
----------------------------------------"
fi

if [ ! -f "$APP_DLL" ]; then
  fail "Build reported success but the expected output was not found:
  $APP_DLL
This is unexpected - check $BUILD_LOG for details."
fi

echo "Starting Cosmic Engine dashboard (no visual yet - pick a scene once it's open)..."
dotnet "$APP_DLL" --dashboard-only > "$LOG_FILE" 2>&1 &
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
  echo "No visual is running yet - choose Stellar Nursery or Lava Lamp from the"
  echo "dashboard to start one. Stellar Nursery uses the known-good show seed"
  echo "(777) automatically when launched this way."
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

  # AUDIT.md Entry 35/36: an empty log here (zero bytes, not even a build
  # error) used to mean macOS AMFI/Gatekeeper killed the locally-built,
  # ad-hoc-signed apphost binary at launch, before any Cosmic Engine code
  # ran - not a Cosmic Engine bug, and nothing our own code could have
  # caught or logged. Entry 37 (Mac AMFI Apphost Workaround Pass) removed
  # that failure mode from this launcher's own launch path: this script now
  # runs `dotnet <path-to-dll>` directly (see "Build, then launch" above),
  # not the rejected apphost, and the .csproj sets <UseAppHost>false</UseAppHost>
  # so that ad-hoc-signed binary isn't even generated anymore. If you're
  # reading this block because the dashboard still didn't come up, the AMFI
  # rejection is very unlikely to be the cause anymore - check the build log
  # and the (likely non-empty, since `dotnet` itself is a trusted/notarized
  # host and should print real Cosmic Engine startup output or a real error)
  # $LOG_FILE first. The AMFI-specific checks below are kept only as a
  # fallback in case something in a future change re-introduces a local
  # apphost dependency (e.g. UseAppHost is reverted, or a different launch
  # path calls the apphost directly).
  BUILT_APP="$SCRIPT_DIR/bin/Debug/net8.0/CosmicEngine.App"
  if [ ! -s "$LOG_FILE" ] && [ -f "$BUILT_APP" ] && command -v spctl >/dev/null 2>&1; then
    if ! spctl -a "$BUILT_APP" >/dev/null 2>&1; then
      DEV_MODE_ON=0
      if command -v DevToolsSecurity >/dev/null 2>&1 && DevToolsSecurity -status 2>/dev/null | grep -qi "enabled"; then
        DEV_MODE_ON=1
      fi
      echo ""
      echo "Likely cause: macOS blocked a locally-built app binary from running at"
      echo "all (confirmed via 'spctl' - Gatekeeper rejects it, and it is not"
      echo "notarized/properly signed). That's why the log above is empty: the"
      echo "process was killed before any Cosmic Engine code executed, so there was"
      echo "nothing for it to print. This is not an audio-device or Cosmic Engine bug."
      echo "Note: this script no longer launches that binary itself (it launches the"
      echo ".dll via the trusted 'dotnet' host instead) - seeing this means something"
      echo "unexpected is still invoking it, or an old apphost binary is stale in bin/."
      echo ""
      if [ "$DEV_MODE_ON" -eq 0 ]; then
        echo "Fix: enable Developer Mode (lets locally-built apps run), then REBOOT"
        echo "your Mac (not just re-run this script) and try again:"
        echo "  sudo DevToolsSecurity -enable"
      else
        echo "Developer Mode is already enabled, but macOS is still blocking this app."
        echo "Most likely fix: REBOOT your Mac, then try again - the code-signing"
        echo "daemon (amfid) may not have picked up Developer Mode being (re-)enabled"
        echo "without a restart, especially right after a macOS update."
        echo ""
        echo "If a reboot doesn't fix it, two more options:"
        echo "  1) Run this project from your internal disk instead of an external"
        echo "     drive (didn't resolve it in our own testing, but worth trying on"
        echo "     your exact setup)."
        echo "  2) Fully disable Gatekeeper (bigger tradeoff - allows ANY unsigned app"
        echo "     to run, not just this one; only do this if you understand and"
        echo "     accept that):"
        echo "       sudo spctl --master-disable"
        echo ""
        echo "Run whichever you choose yourself - this script won't do it for you."
      fi
      echo ""
    fi
  fi

  # If the log genuinely has content but the dashboard still never came up,
  # that's a real Cosmic Engine startup problem (e.g. no audio capture
  # device), not AMFI/Gatekeeper - the tail above already shows it.

  read -r -p "Press Enter to close this window..." _ 2>/dev/null
  exit 1
fi
