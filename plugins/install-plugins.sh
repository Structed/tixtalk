#!/bin/bash
# Plugin installer for pretix/pretalx containers.
# Installs MeetToMatch plugins from mounted volumes, then calls the
# ORIGINAL image entrypoint so migrations, static files, and process
# management proceed normally.
set -e

PLUGIN_DIR="/opt/plugins"

if [ -d "$PLUGIN_DIR/meettomatch-client" ]; then
    echo "[meettomatch] Installing shared client library..."
    pip install -q "$PLUGIN_DIR/meettomatch-client"
fi

if [ -d "$PLUGIN_DIR/pretix-meettomatch" ]; then
    echo "[meettomatch] Installing pretix-meettomatch plugin..."
    pip install --no-deps -q "$PLUGIN_DIR/pretix-meettomatch"
fi

if [ -d "$PLUGIN_DIR/pretalx-meettomatch" ]; then
    echo "[meettomatch] Installing pretalx-meettomatch plugin..."
    pip install --no-deps -q "$PLUGIN_DIR/pretalx-meettomatch"
fi

# Call the original container entrypoint.
# Both pretix/standalone and pretalx/standalone use /entrypoint.sh
if [ -f /entrypoint.sh ]; then
    exec /entrypoint.sh "$@"
elif [ -f /usr/local/bin/entrypoint.sh ]; then
    exec /usr/local/bin/entrypoint.sh "$@"
else
    # Fallback: just run whatever was passed as CMD
    exec "$@"
fi
