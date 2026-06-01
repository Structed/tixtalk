# MeetToMatch Integration Plugins

This directory contains the MeetToMatch integration plugins for Pretix and Pretalx.

## Structure

```
plugins/
├── meettomatch-client/     # Shared Python API client library
├── pretix-meettomatch/     # Pretix plugin (order → account creation)
├── pretalx-meettomatch/    # Pretalx plugin (schedule → sync)
└── install-plugins.sh      # Startup script to install plugins in containers
```

## How It Works

### Pretix Plugin (`pretix-meettomatch`)

When a ticket is purchased:
1. Listens to `order_placed` / `order_paid` / `order_changed` signals
2. Checks if the order contains a MeetToMatch product, add-on, or opt-in checkbox
3. Creates the appropriate MeetToMatch account:
   - **Full Networking**: visible, in networking group (can arrange meetings)
   - **Schedule Viewer**: invisible, in viewer group (can browse schedule + attendees)
4. Generates an SSO one-click login link
5. Stores result in order metadata for email templates

### Pretalx Plugin (`pretalx-meettomatch`)

When a schedule is published:
1. Listens to `schedule_release` signal
2. Syncs all speakers to MeetToMatch (upsert by `source_id`)
3. Syncs all sessions/talks to MeetToMatch (upsert by `source_id`)
4. Removes stale sessions that are no longer in the schedule

Manual sync: `python -m pretalx mtm_sync <event_slug>`

## Configuration

Both plugins read settings from the Pretix/Pretalx event settings UI:

- **MeetToMatch API Key** — private key from MeetToMatch
- **MeetToMatch Event ID** — event identifier from MeetToMatch
- **Base URL** — defaults to `https://my.meettomatch.com/public/api`
- **Group IDs** — networking group and schedule-viewer group (from MTM admin)
- **Product IDs** — which Pretix products/add-ons trigger account creation

## Deployment

Plugins are mounted as read-only volumes in `docker-compose.yml` and installed
at container startup via `install-plugins.sh`. The `requests` library (used by
the API client) is already present in both pretix and pretalx containers.

## Prerequisites

1. Obtain MeetToMatch API key and event ID (contact MeetToMatch)
2. Configure products and questions in Pretix admin
3. Set group IDs in plugin settings (match MeetToMatch admin configuration)
