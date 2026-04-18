#!/bin/bash
# =============================================================================
# Pretix + Pretalx Management CLI
# =============================================================================
# Usage:
#   ./manage.sh                           # Interactive menu
#   ./manage.sh <command> [args]          # Direct command
#   ./manage.sh remote <host> [command]   # Run on remote server via SSH
# =============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SCRIPTS="$SCRIPT_DIR/scripts"

# Source common library
source "$SCRIPT_DIR/scripts/lib/common.sh" 2>/dev/null || true
PROJECT_DIR="$SCRIPT_DIR"

# ---- Commands ----------------------------------------------------------------

cmd_setup() {
    echo "This will install Docker and configure the firewall."
    echo "Requires root/sudo."
    echo ""
    sudo bash "$SCRIPTS/setup.sh"
}

cmd_deploy() {
    bash "$SCRIPTS/deploy.sh"
}

cmd_status() {
    load_env
    cd "$SCRIPT_DIR"

    echo "=== Pretix + Pretalx Status ==="
    echo ""

    if [ -n "${DOMAIN:-}" ] && [ "$DOMAIN" != "yourdomain.com" ]; then
        echo "  Pretix:  https://tickets.${DOMAIN}"
        echo "  Pretalx: https://talks.${DOMAIN}"
        echo ""
    fi

    docker compose ps --format "table {{.Name}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}" 2>/dev/null || echo "  (services not running)"
    echo ""

    # Disk usage
    echo "Volumes:"
    docker system df -v 2>/dev/null | grep -E "^(VOLUME|local)" | head -10 || true
    echo ""

    # Backup info
    if [ -d "$SCRIPT_DIR/backups" ]; then
        local count
        count=$(find "$SCRIPT_DIR/backups" -name "*.sql.gz" 2>/dev/null | wc -l)
        local latest
        latest=$(ls -t "$SCRIPT_DIR/backups"/*.sql.gz 2>/dev/null | head -1 || echo "none")
        echo "Backups: ${count} files, latest: $(basename "${latest}" 2>/dev/null || echo 'none')"
    else
        echo "Backups: not configured (run: ./manage.sh backup --install-cron)"
    fi
}

cmd_update() {
    cd "$SCRIPT_DIR"
    bash "$SCRIPTS/update.sh" "$@"
}

cmd_logs() {
    cd "$SCRIPT_DIR"
    local service="${1:-}"
    if [ -n "$service" ]; then
        docker compose logs "$service" --tail 100 -f
    else
        docker compose logs --tail 50 -f
    fi
}

cmd_backup() {
    bash "$SCRIPTS/backup.sh" "$@"
}

cmd_restore() {
    cd "$SCRIPT_DIR"

    if [ $# -ge 2 ]; then
        bash "$SCRIPTS/restore.sh" "$@"
        return
    fi

    # Interactive: list backups and let user choose
    local backups
    backups=$(ls -t "$SCRIPT_DIR/backups"/*.sql.gz 2>/dev/null || true)
    if [ -z "$backups" ]; then
        echo "No backups found in backups/"
        exit 1
    fi

    echo "Available backups:"
    echo ""
    local i=1
    local files=()
    while IFS= read -r f; do
        files+=("$f")
        echo "  $i) $(basename "$f") ($(du -h "$f" | cut -f1))"
        i=$((i + 1))
    done <<< "$backups"

    echo ""
    read -p "Select backup number: " num
    if [ -z "$num" ] || [ "$num" -lt 1 ] || [ "$num" -gt "${#files[@]}" ] 2>/dev/null; then
        echo "Invalid selection."
        exit 1
    fi

    local selected="${files[$((num - 1))]}"
    echo ""
    echo "Which database?"
    echo "  1) pretix"
    echo "  2) pretalx"
    read -p "Select: " db_num

    local db_name
    case "$db_num" in
        1) db_name="pretix" ;;
        2) db_name="pretalx" ;;
        *) echo "Invalid selection."; exit 1 ;;
    esac

    bash "$SCRIPTS/restore.sh" "$selected" "$db_name"
}

cmd_shell() {
    cd "$SCRIPT_DIR"
    local service="${1:-pretix}"
    echo "Opening shell in ${service}..."
    docker compose exec "$service" /bin/bash 2>/dev/null || docker compose exec "$service" /bin/sh
}

cmd_dns() {
    bash "$SCRIPTS/cloudflare-dns.sh"
}

cmd_restart() {
    cd "$SCRIPT_DIR"
    echo "Restarting services..."
    $(compose_cmd) restart
    echo "Done."
}

cmd_stop() {
    cd "$SCRIPT_DIR"
    echo "Stopping services..."
    $(compose_cmd) stop
    echo "All services stopped."
}

cmd_start() {
    cd "$SCRIPT_DIR"
    echo "Starting services..."
    $(compose_cmd) up -d
    echo "All services started."
}

cmd_remote() {
    if [ $# -lt 1 ]; then
        echo "Usage: ./manage.sh remote <user@host> [command] [args...]"
        echo ""
        echo "Examples:"
        echo "  ./manage.sh remote root@1.2.3.4           # Interactive menu"
        echo "  ./manage.sh remote root@1.2.3.4 status    # Direct command"
        echo "  ./manage.sh remote root@1.2.3.4 logs pretix"
        exit 1
    fi

    local host="$1"
    shift

    # Find project directory on remote (default: ~/pre-talx-tix-azure)
    local remote_dir="~/pre-talx-tix-azure"

    if [ $# -eq 0 ]; then
        # Interactive: SSH with TTY and run manage.sh
        ssh -t "$host" "cd $remote_dir && ./manage.sh"
    else
        # Direct command: pass through
        ssh -t "$host" "cd $remote_dir && ./manage.sh $*"
    fi
}

cmd_help() {
    cat <<'EOF'
Pretix + Pretalx Management CLI

Usage: ./manage.sh [command] [args...]

Commands:
  setup                Install Docker & configure firewall (run once)
  deploy               First-time deployment (generates secrets, starts services)
  status               Show service status, URLs, and disk usage
  update [--pretix TAG] [--pretalx TAG]
                       Pull latest images and restart
  logs [service]       Tail logs (all services or specific one)
  backup [--install-cron]
                       Back up databases (or install daily cron job)
  restore [file db]    Restore database from backup (interactive if no args)
  shell [service]      Open a shell in a container (default: pretix)
  dns                  Create/update Cloudflare DNS records
  restart              Restart all services
  stop                 Stop all services
  start                Start all services
  remote <host> [cmd]  Run a command on the remote server via SSH
  help                 Show this help message

Run without arguments for an interactive menu.
EOF
}

# ---- Interactive Menu --------------------------------------------------------

show_menu() {
    load_env

    local domain_info=""
    if [ -n "${DOMAIN:-}" ] && [ "$DOMAIN" != "yourdomain.com" ]; then
        domain_info=" (${DOMAIN})"
    fi

    echo ""
    echo "╔══════════════════════════════════════════╗"
    echo "║   Pretix + Pretalx Manager${domain_info}"
    echo "╠══════════════════════════════════════════╣"

    # Check if services are running
    cd "$SCRIPT_DIR"
    local running
    running=$(docker compose ps --status running -q 2>/dev/null | wc -l)

    if [ "$running" -gt 0 ]; then
        echo "║   Services: ${running} running                  "
    else
        echo "║   Services: not running                  "
    fi

    echo "╠══════════════════════════════════════════╣"
    echo "║                                          ║"
    echo "║   1) Status          5) Backup            "
    echo "║   2) Update          6) Restore           "
    echo "║   3) Logs            7) Shell             "
    echo "║   4) Restart         8) DNS records        "
    echo "║                                           "
    echo "║   s) Setup (first time)                   "
    echo "║   d) Deploy (first time)                  "
    echo "║   q) Quit                                 "
    echo "║                                          ║"
    echo "╚══════════════════════════════════════════╝"
    echo ""
    read -p "Choose: " choice

    case "$choice" in
        1) cmd_status ;;
        2)
            read -p "Pretix tag (leave empty to keep current): " ptag
            read -p "Pretalx tag (leave empty to keep current): " xtag
            local args=()
            [ -n "$ptag" ] && args+=(--pretix "$ptag")
            [ -n "$xtag" ] && args+=(--pretalx "$xtag")
            cmd_update "${args[@]+"${args[@]}"}"
            ;;
        3)
            echo "Services: caddy, postgres, redis, pretix, pretalx"
            read -p "Service (leave empty for all): " svc
            cmd_logs "$svc"
            ;;
        4) cmd_restart ;;
        5)
            echo "  1) Run backup now"
            echo "  2) Install daily cron job"
            read -p "Choose: " bchoice
            case "$bchoice" in
                1) cmd_backup ;;
                2) cmd_backup --install-cron ;;
                *) echo "Invalid choice." ;;
            esac
            ;;
        6) cmd_restore ;;
        7)
            echo "Services: pretix, pretalx, postgres, redis, caddy"
            read -p "Service (default: pretix): " svc
            cmd_shell "${svc:-pretix}"
            ;;
        8) cmd_dns ;;
        s|S) cmd_setup ;;
        d|D) cmd_deploy ;;
        q|Q) echo "Bye!" ;;
        *) echo "Invalid choice." ;;
    esac
}

# ---- Entry Point -------------------------------------------------------------

cd "$SCRIPT_DIR"

if [ $# -eq 0 ]; then
    show_menu
else
    case "$1" in
        setup)    shift; cmd_setup "$@" ;;
        deploy)   shift; cmd_deploy "$@" ;;
        status)   shift; cmd_status "$@" ;;
        update)   shift; cmd_update "$@" ;;
        logs)     shift; cmd_logs "$@" ;;
        backup)   shift; cmd_backup "$@" ;;
        restore)  shift; cmd_restore "$@" ;;
        shell)    shift; cmd_shell "$@" ;;
        dns)      shift; cmd_dns "$@" ;;
        restart)  shift; cmd_restart "$@" ;;
        stop)     shift; cmd_stop "$@" ;;
        start)    shift; cmd_start "$@" ;;
        remote)   shift; cmd_remote "$@" ;;
        help|-h|--help) cmd_help ;;
        *)
            echo "Unknown command: $1"
            echo "Run './manage.sh help' for usage."
            exit 1
            ;;
    esac
fi
