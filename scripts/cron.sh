#!/bin/bash
# Run periodic tasks for pretix and pretalx.
# Both apps require `runperiodic` to process background tasks like sending
# emails, expiring orders, cleaning up data, etc. The standalone Docker images
# do NOT run this automatically — it must be scheduled externally.
#
# Usage:
#   ./scripts/cron.sh                 # Run periodic tasks now
#   ./scripts/cron.sh --install       # Install cron job (every 5 minutes)
#   ./scripts/cron.sh --remove        # Remove the cron job
set -euo pipefail

# Ensure PATH includes Docker (cron runs with minimal PATH)
export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH"

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/lib/common.sh"

init_project_dir
cd "$PROJECT_DIR"

# Install cron job if requested
if [ "${1:-}" = "--install" ]; then
    LOG_DIR="$PROJECT_DIR/logs"
    mkdir -p "$LOG_DIR"
    CRON_CMD="*/5 * * * * mkdir -p $LOG_DIR && cd $PROJECT_DIR && ./scripts/cron.sh >> $LOG_DIR/cron.log 2>&1 # tixtalk-cron"
    ( crontab -l 2>/dev/null || true ) | ( grep -v "# tixtalk-cron" || true ) | ( grep -v "tixtalk-cron\.log" || true ) | { cat; echo "$CRON_CMD"; } | crontab -
    log "Installed periodic task cron job (every 5 minutes)."
    echo "Logs: $LOG_DIR/cron.log"
    exit 0
fi

# Remove cron job if requested
if [ "${1:-}" = "--remove" ]; then
    ( crontab -l 2>/dev/null || true ) | ( grep -v "# tixtalk-cron" || true ) | ( grep -v "tixtalk-cron\.log" || true ) | crontab -
    log "Removed periodic task cron job."
    exit 0
fi

log "Running periodic tasks..."

# Run pretix periodic tasks (sends emails, expires orders, processes payments, etc.)
if docker compose exec -T pretix pretix cron 2>&1; then
    echo "  pretix runperiodic OK"
else
    log_warn "pretix runperiodic failed"
fi

# Run pretalx periodic tasks (sends emails, processes notifications, etc.)
if docker compose exec -T pretalx pretalx cron 2>&1; then
    echo "  pretalx runperiodic OK"
else
    log_warn "pretalx runperiodic failed"
fi

log "Periodic tasks complete."
