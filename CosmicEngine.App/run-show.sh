#!/bin/bash
# Scene Dashboard v0.1 - simple "show mode" launcher.
#
# This is NOT a bounded diagnostic - it starts Cosmic Engine at the Safe profile
# and lets it run until you quit it (via the dashboard's "Quit Engine" button, or
# Ctrl+C in this terminal). Use `dotnet run -- --smoke-test`/`--diagnostic ...`
# instead for bounded, auto-exiting runs.
set -e
cd "$(dirname "$0")"

echo "Starting Cosmic Engine (Safe profile)..."
echo "Dashboard: http://localhost:8080"

dotnet run -- --profile Safe &
ENGINE_PID=$!

# Give the window and control server a moment to start before opening the browser.
sleep 3
if command -v open >/dev/null 2>&1; then
  open "http://localhost:8080" 2>/dev/null || echo "Could not auto-open browser. Dashboard: http://localhost:8080"
else
  echo "Dashboard: http://localhost:8080"
fi

wait "$ENGINE_PID"
