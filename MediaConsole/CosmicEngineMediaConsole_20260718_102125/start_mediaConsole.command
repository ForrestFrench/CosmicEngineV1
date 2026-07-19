#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
CONSOLE_URL="http://127.0.0.1:8140/"
HEALTH_URL="${CONSOLE_URL}api/health"
LOG_FILE="/private/tmp/cosmic_media_console.log"
PID_FILE="/private/tmp/cosmic_media_console.pid"
CORE_URL="http://localhost:8080/audio/reactivity"
CORE_DIR="$(cd "$SCRIPT_DIR/../../CosmicEngineApp" 2>/dev/null && pwd || true)"
CORE_LOG_FILE="/private/tmp/cosmic_audio_core.log"
CORE_PID_FILE="/private/tmp/cosmic_audio_core.pid"

cd "$SCRIPT_DIR"

start_audio_core() {
  if /usr/bin/curl -fsS --max-time 1 "$CORE_URL" >/dev/null 2>&1; then
    return 0
  fi

  if [ -z "$CORE_DIR" ] || [ ! -d "$CORE_DIR" ]; then
    echo "Audio core folder was not found next to the Media Console."
    echo "Expected: $SCRIPT_DIR/../../CosmicEngineApp"
    return 1
  fi

  local dotnet_bin
  dotnet_bin="$(command -v dotnet || true)"
  if [ -z "$dotnet_bin" ]; then
    echo "The .NET runtime required by the existing Cosmic Engine audio core was not found."
    return 1
  fi

  local core_dll="$CORE_DIR/bin/Debug/net8.0/CosmicEngine.App.dll"
  if [ ! -f "$core_dll" ]; then
    echo "Building the existing Cosmic Engine audio core once..."
    if ! (cd "$CORE_DIR" && "$dotnet_bin" build >"$CORE_LOG_FILE" 2>&1); then
      echo "Audio core build failed. Diagnostic log: $CORE_LOG_FILE"
      return 1
    fi
  fi

  (
    cd "$CORE_DIR"
    /usr/bin/nohup "$dotnet_bin" "$core_dll" --dashboard-only >"$CORE_LOG_FILE" 2>&1 &
    echo $! >"$CORE_PID_FILE"
  )

  for attempt in {1..75}; do
    if /usr/bin/curl -fsS --max-time 1 "$CORE_URL" >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.2
  done

  echo "Audio core did not start. Diagnostic log: $CORE_LOG_FILE"
  return 1
}

if ! start_audio_core; then
  echo "The Media Console will open, but Audio Tuning will remain offline until the audio core issue above is resolved."
fi

if /usr/bin/curl -fsS "$HEALTH_URL" >/dev/null 2>&1; then
  /usr/bin/open "$CONSOLE_URL"
  exit 0
fi

/usr/bin/nohup /usr/bin/env \
  PYTHONPYCACHEPREFIX="/private/tmp/cosmic_media_console_pycache" \
  /usr/bin/python3 media_console.py --host 127.0.0.1 --port 8140 --quiet \
  >"$LOG_FILE" 2>&1 &

echo $! >"$PID_FILE"

for attempt in {1..50}; do
  if /usr/bin/curl -fsS "$HEALTH_URL" >/dev/null 2>&1; then
    /usr/bin/open "$CONSOLE_URL"
    exit 0
  fi
  sleep 0.2
done

echo "Cosmic Engine Media Console did not start."
echo "Diagnostic log: $LOG_FILE"
read -r -p "Press Return to close this window."
exit 1
