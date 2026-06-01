"""Schedule and speaker synchronization logic for MeetToMatch.

Handles the full sync flow: speakers first, then sessions (so speaker
source_ids are available for session linking).
"""

import logging

from meettomatch import MeetToMatchClient
from meettomatch.models import MTMSession, MTMSpeaker

logger = logging.getLogger(__name__)


def get_mtm_client(event) -> MeetToMatchClient | None:
    """Create a MeetToMatchClient from event settings."""
    api_key = event.settings.get("meettomatch_api_key", "")
    event_id = event.settings.get("meettomatch_event_id", "")

    if not api_key or not event_id:
        return None

    base_url = event.settings.get(
        "meettomatch_base_url", "https://my.meettomatch.com/public/api"
    )
    return MeetToMatchClient(api_key=api_key, event_id=event_id, base_url=base_url)


def sync_schedule_to_meettomatch(event, schedule):
    """Sync the released schedule to MeetToMatch.

    Flow:
    1. Sync speakers (so their source_ids exist in MTM)
    2. Sync sessions (linking to speakers by source_id)
    3. Remove sessions from MTM that are no longer in the schedule
    """
    client = get_mtm_client(event)
    if client is None:
        logger.warning(f"MeetToMatch not configured for event {event.slug}")
        return

    sync_speakers_enabled = event.settings.get("meettomatch_sync_speakers", "true").lower() in (
        "true",
        "1",
        "yes",
    )

    # Get talks from the released schedule
    talks = _get_scheduled_talks(schedule)
    if not talks:
        logger.info(f"No talks found in schedule for {event.slug}")
        return

    # Step 1: Sync speakers
    speaker_source_ids = set()
    if sync_speakers_enabled:
        speakers = _collect_speakers(talks)
        for speaker_data in speakers:
            _upsert_speaker(client, speaker_data)
            speaker_source_ids.add(speaker_data["source_id"])

    # Step 2: Sync sessions
    synced_source_ids = set()
    session_groups = event.settings.get("meettomatch_session_groups", "")
    groups = [g.strip() for g in session_groups.split(",") if g.strip()] if session_groups else []

    for talk in talks:
        source_id = _sync_talk(client, talk, groups)
        if source_id:
            synced_source_ids.add(source_id)

    # Step 3: Remove deleted sessions
    _remove_stale_sessions(client, event, synced_source_ids)

    logger.info(
        f"MeetToMatch sync complete for {event.slug}: "
        f"{len(synced_source_ids)} sessions, {len(speaker_source_ids)} speakers"
    )


def _get_scheduled_talks(schedule):
    """Get all confirmed talks from the schedule."""
    try:
        return list(schedule.talks.filter(is_visible=True).select_related(
            "submission", "submission__submission_type", "room"
        ).prefetch_related("submission__speakers"))
    except AttributeError:
        # Fallback for different pretalx versions
        try:
            from pretalx.schedule.models import TalkSlot
            return list(TalkSlot.objects.filter(
                schedule=schedule, is_visible=True
            ).select_related(
                "submission", "submission__submission_type", "room"
            ).prefetch_related("submission__speakers"))
        except Exception as e:
            logger.error(f"Failed to get talks from schedule: {e}")
            return []


def _collect_speakers(talks) -> list[dict]:
    """Collect unique speakers from all talks."""
    seen = set()
    speakers = []

    for talk in talks:
        submission = talk.submission
        if not submission:
            continue

        for speaker in submission.speakers.all():
            if speaker.code in seen:
                continue
            seen.add(speaker.code)

            speakers.append({
                "source_id": speaker.code,
                "email": speaker.email or "",
                "firstname": speaker.name.split(" ", 1)[0] if speaker.name else "",
                "lastname": speaker.name.split(" ", 1)[1] if speaker.name and " " in speaker.name else "",
                "bio": getattr(speaker, "biography", "") or "",
            })

    return speakers


def _upsert_speaker(client: MeetToMatchClient, speaker_data: dict):
    """Create or update a speaker in MeetToMatch."""
    source_id = speaker_data["source_id"]

    existing = client.get_speaker(source_id)
    if existing:
        client.update_speaker(
            source_id=source_id,
            firstname=speaker_data["firstname"],
            lastname=speaker_data["lastname"],
            bio=speaker_data.get("bio", ""),
        )
        logger.debug(f"Updated speaker {source_id}")
    else:
        speaker = MTMSpeaker(
            firstname=speaker_data["firstname"],
            lastname=speaker_data["lastname"],
            source_id=source_id,
            email=speaker_data.get("email", ""),
            bio=speaker_data.get("bio", ""),
        )
        client.create_speaker(speaker)
        logger.debug(f"Created speaker {source_id}")


def _sync_talk(client: MeetToMatchClient, talk, groups: list[str]) -> str | None:
    """Sync a single talk to MeetToMatch. Returns the source_id if successful."""
    submission = talk.submission
    if not submission:
        return None

    source_id = submission.code
    if not talk.start or not talk.end:
        logger.warning(f"Talk {source_id} has no start/end time, skipping")
        return None

    # Collect speaker source_ids for this talk
    speaker_source_ids = [s.code for s in submission.speakers.all()]

    session = MTMSession(
        title=submission.title,
        date=talk.start.strftime("%Y-%m-%d"),
        start=talk.start.strftime("%H:%M"),
        end=talk.end.strftime("%H:%M"),
        source_id=source_id,
        info=submission.abstract or submission.description or "",
        type=submission.submission_type.name if submission.submission_type else "",
        location=talk.room.name if talk.room else "",
        speakers_by_source_id=speaker_source_ids,
        groups=groups,
        location_type="onsite",
    )

    existing = client.get_session(source_id)
    if existing:
        client.update_session(
            source_id=source_id,
            title=session.title,
            date=session.date,
            start=session.start,
            end=session.end,
            info=session.info,
            type=session.type,
            location=session.location,
            speakersBySourceId=session.speakers_by_source_id,
        )
        logger.debug(f"Updated session {source_id}")
    else:
        client.create_session(session)
        logger.debug(f"Created session {source_id}")

    return source_id


def _remove_stale_sessions(client: MeetToMatchClient, event, current_source_ids: set[str]):
    """Remove sessions from MeetToMatch that are no longer in the schedule.

    Only removes sessions that have a source_id (i.e., were created by this
    integration). Manually managed sessions in MeetToMatch are left untouched.
    """
    try:
        result = client.list_sessions()
        mtm_sessions = result.get("data", result.get("sessions", []))

        if not isinstance(mtm_sessions, list):
            return

        for mtm_session in mtm_sessions:
            sid = mtm_session.get("source_id", "")
            # Only delete sessions that have a source_id (managed by us)
            # and are no longer in the current schedule
            if sid and sid not in current_source_ids:
                try:
                    client.delete_session(sid)
                    logger.info(f"Deleted stale MeetToMatch session: {sid}")
                except Exception as e:
                    logger.warning(f"Failed to delete stale session {sid}: {e}")

    except Exception as e:
        logger.warning(f"Failed to clean up stale sessions: {e}")
