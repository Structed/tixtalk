#!/bin/bash
# =============================================================================
# Common Library for Pretix + Pretalx Scripts
# =============================================================================
# Source this file in your scripts:
#   SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
#   source "$SCRIPT_DIR/lib/common.sh" 2>/dev/null || source "$SCRIPT_DIR/../lib/common.sh"

# Find the project root directory (contains docker-compose.yml)
find_project_root() {
    local dir="${1:-$(pwd)}"
    while [[ "$dir" != "/" ]]; do
        if [[ -f "$dir/docker-compose.yml" ]]; then
            echo "$dir"
            return 0
        fi
        dir="$(dirname "$dir")"
    done
    return 1
}

# Initialize PROJECT_DIR if not set
init_project_dir() {
    if [[ -z "${PROJECT_DIR:-}" ]]; then
        PROJECT_DIR="$(find_project_root)"
        if [[ -z "$PROJECT_DIR" ]]; then
            echo "ERROR: Could not find project root (docker-compose.yml)" >&2
            exit 1
        fi
    fi
}

# Load .env file into environment
# Usage: load_env [path_to_env_file]
load_env() {
    init_project_dir
    local env_file="${1:-$PROJECT_DIR/.env}"
    
    if [[ -f "$env_file" ]]; then
        set -a
        # shellcheck source=/dev/null
        source "$env_file"
        set +a
        return 0
    fi
    return 1
}

# Get the correct docker compose command based on configuration
# Returns: "docker compose" or "docker compose -f docker-compose.yml -f docker-compose.cloudflare.yml"
compose_cmd() {
    init_project_dir
    
    # Load env if not already loaded
    if [[ -z "${CLOUDFLARE_DNS_CHALLENGE:-}" ]] && [[ -f "$PROJECT_DIR/.env" ]]; then
        load_env
    fi
    
    if [[ "${CLOUDFLARE_DNS_CHALLENGE:-false}" == "true" ]]; then
        echo "docker compose -f docker-compose.yml -f docker-compose.cloudflare.yml"
    else
        echo "docker compose"
    fi
}

# Wait for a service to become ready with retries
# Usage: wait_for_service <name> <check_command> [max_attempts] [sleep_seconds]
# Example: wait_for_service "PostgreSQL" "docker compose exec -T postgres pg_isready -U user" 30 5
wait_for_service() {
    local name="$1"
    local check_cmd="$2"
    local max_attempts="${3:-30}"
    local sleep_secs="${4:-5}"
    
    echo "Waiting for $name..."
    for i in $(seq 1 "$max_attempts"); do
        if eval "$check_cmd" >/dev/null 2>&1; then
            echo "$name is ready."
            return 0
        fi
        if [[ "$i" -eq "$max_attempts" ]]; then
            echo "ERROR: $name timeout after $((max_attempts * sleep_secs)) seconds"
            return 1
        fi
        echo "  Waiting for $name... ($i/$max_attempts)"
        sleep "$sleep_secs"
    done
}

# Wait for Docker daemon to be ready
# Usage: wait_for_docker [max_attempts]
wait_for_docker() {
    local max_attempts="${1:-30}"
    wait_for_service "Docker" "docker info" "$max_attempts" 2
}

# Wait for a database to exist in PostgreSQL
# Usage: wait_for_database <db_name> <db_user> [max_attempts] [sleep_seconds]
wait_for_database() {
    local db_name="$1"
    local db_user="$2"
    local max_attempts="${3:-60}"
    local sleep_secs="${4:-5}"
    
    local check_cmd="docker compose exec -T postgres psql -U $db_user -tAc \"SELECT 1 FROM pg_database WHERE datname='$db_name'\" | grep -q 1"
    wait_for_service "database '$db_name'" "$check_cmd" "$max_attempts" "$sleep_secs"
}

# Generate a cryptographically secure random string
# Usage: generate_secret [length]
generate_secret() {
    local length="${1:-32}"
    openssl rand -base64 48 | tr -d '/+=' | head -c "$length"
}

# Log message with timestamp
# Usage: log <message>
log() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*"
}

# Log error message with timestamp to stderr
# Usage: log_error <message>
log_error() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] ERROR: $*" >&2
}

# Log warning message with timestamp
# Usage: log_warn <message>
log_warn() {
    echo "[$(date '+%Y-%m-%d %H:%M:%S')] WARNING: $*"
}
