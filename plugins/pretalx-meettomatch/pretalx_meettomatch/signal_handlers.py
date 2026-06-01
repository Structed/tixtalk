"""Signal handlers for the pretalx-meettomatch plugin.

Listens to schedule_release signal to trigger MeetToMatch sync.
"""

import logging

from django.dispatch import receiver

from pretalx.schedule.signals import schedule_release

from .sync import sync_schedule_to_meettomatch

logger = logging.getLogger(__name__)


@receiver(schedule_release, dispatch_uid="meettomatch_schedule_release")
def on_schedule_release(sender, schedule, **kwargs):
    """Triggered when a schedule version is released/published."""
    event = schedule.event if hasattr(schedule, "event") else sender

    api_key = event.settings.get("meettomatch_api_key", "")
    if not api_key:
        return

    logger.info(
        f"MeetToMatch: schedule_release signal for event {event.slug}, "
        f"version {getattr(schedule, 'version', 'unknown')}"
    )

    try:
        sync_schedule_to_meettomatch(event, schedule)
    except Exception as e:
        logger.exception(f"MeetToMatch: Failed to sync schedule for {event.slug}: {e}")
