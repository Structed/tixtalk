"""Factory for creating MeetToMatch API client instances from event settings."""

import logging

from meettomatch import MeetToMatchClient

logger = logging.getLogger(__name__)

DEFAULT_BASE_URL = "https://my.meettomatch.com/public/api"


def get_mtm_client(event) -> MeetToMatchClient | None:
    """Create a MeetToMatchClient from the event's plugin settings.

    Returns None if the plugin is not configured (missing API key or event ID).
    """
    api_key = event.settings.get("meettomatch_api_key", default="")
    event_id = event.settings.get("meettomatch_event_id", default="")

    if not api_key or not event_id:
        return None

    base_url = event.settings.get("meettomatch_base_url", default=DEFAULT_BASE_URL)

    return MeetToMatchClient(
        api_key=api_key,
        event_id=event_id,
        base_url=base_url,
    )
