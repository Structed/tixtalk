"""MeetToMatch API client with retry logic and error handling."""

import logging
import time
from dataclasses import asdict

import requests

from meettomatch.exceptions import (
    MeetToMatchAPIError,
    MeetToMatchAuthError,
    MeetToMatchNotFoundError,
)
from meettomatch.models import MTMSession, MTMSpeaker, MTMUser

logger = logging.getLogger(__name__)

DEFAULT_BASE_URL = "https://my.meettomatch.com/public/api"
MAX_RETRIES = 3
RETRY_BACKOFF = 2  # seconds, doubles each retry


class MeetToMatchClient:
    """Client for the MeetToMatch public API.

    All endpoints use POST with JSON body. Authentication is via
    api_key + event_id passed in every request body.
    """

    def __init__(self, api_key: str, event_id: str, base_url: str = DEFAULT_BASE_URL):
        self.api_key = api_key
        self.event_id = event_id
        self.base_url = base_url.rstrip("/")
        self.session = requests.Session()
        self.session.headers.update({"Content-Type": "application/json"})

    def _request(self, endpoint: str, data: dict, retries: int = MAX_RETRIES) -> dict:
        """Make a POST request to the MeetToMatch API with retry logic."""
        url = f"{self.base_url}/{endpoint.strip('/')}/"
        payload = {
            "api_key": self.api_key,
            "event_id": self.event_id,
            **data,
        }

        last_exception = None
        for attempt in range(retries):
            try:
                response = self.session.post(url, json=payload, timeout=30)

                # Retry on transient server errors (5xx)
                if response.status_code >= 500 and attempt < retries - 1:
                    wait = RETRY_BACKOFF * (2**attempt)
                    logger.warning(
                        f"MeetToMatch API returned {response.status_code} on {endpoint} "
                        f"(attempt {attempt + 1}), retrying in {wait}s"
                    )
                    time.sleep(wait)
                    continue

                return self._handle_response(response, endpoint)
            except (requests.ConnectionError, requests.Timeout) as e:
                last_exception = e
                if attempt < retries - 1:
                    wait = RETRY_BACKOFF * (2**attempt)
                    logger.warning(
                        f"MeetToMatch API request to {endpoint} failed (attempt {attempt + 1}), "
                        f"retrying in {wait}s: {e}"
                    )
                    time.sleep(wait)

        raise MeetToMatchAPIError(
            f"Failed to reach MeetToMatch API after {retries} attempts: {last_exception}"
        )

    def _handle_response(self, response: requests.Response, endpoint: str) -> dict:
        """Parse API response and raise appropriate exceptions."""
        try:
            data = response.json()
        except ValueError:
            raise MeetToMatchAPIError(
                f"Invalid JSON response from {endpoint}",
                status_code=response.status_code,
            )

        if response.status_code == 403 or data.get("error") == "PERMISSION_DENIED":
            raise MeetToMatchAuthError(
                "Authentication failed — check api_key and event_id",
                status_code=response.status_code,
                response_data=data,
            )

        if response.status_code == 404 or data.get("error") == "NOT_FOUND":
            raise MeetToMatchNotFoundError(
                f"Resource not found: {endpoint}",
                status_code=response.status_code,
                response_data=data,
            )

        if not response.ok or data.get("error"):
            raise MeetToMatchAPIError(
                f"API error on {endpoint}: {data.get('error', response.status_code)}",
                status_code=response.status_code,
                response_data=data,
            )

        return data

    # ─── User/Attendee Endpoints ───────────────────────────────────────

    def create_user(self, user: MTMUser, send_email: bool = False) -> dict:
        """Create a new attendee in MeetToMatch.

        Returns the API response containing the created user data.
        """
        payload = {
            "email": user.email,
            "firstname": user.firstname,
            "lastname": user.lastname,
            "company_name": user.company_name,
            "group_id": user.group_id,
            "visible": user.visible,
            "active": user.active,
            "sendemail": 1 if send_email else 0,
        }
        if user.function:
            payload["function"] = user.function
        if user.profile:
            payload["profile"] = user.profile
        if user.web:
            payload["web"] = user.web
        if user.locale:
            payload["locale"] = user.locale

        logger.info(f"Creating MeetToMatch user: {user.email}")
        return self._request("user/create", payload)

    def get_user(self, email: str) -> dict | None:
        """Look up a user by email. Returns None if not found."""
        try:
            return self._request("user/get", {"email": email})
        except MeetToMatchNotFoundError:
            return None

    def update_user(self, email: str, **fields) -> dict:
        """Update an existing user's fields."""
        payload = {"email": email, **fields}
        logger.info(f"Updating MeetToMatch user: {email}, fields: {list(fields.keys())}")
        return self._request("user/update", payload)

    def get_sso_token(self, email: str) -> str:
        """Get a one-time SSO login token for a user.

        Returns the token string that can be used in a login redirect URL.
        """
        result = self._request("user/gettoken", {"email": email})
        token = result.get("token") or result.get("data", {}).get("token")
        if not token:
            raise MeetToMatchAPIError(f"No token returned for {email}", response_data=result)
        return token

    def build_sso_url(self, email: str, redirect_url: str = "") -> str:
        """Generate a full SSO login URL for a user."""
        from urllib.parse import urlencode, quote

        token = self.get_sso_token(email)
        base = self.base_url.replace("/public/api", "") + "/loginredirect/"
        params = {"token": token, "email": email}
        if redirect_url:
            params["redirecturl"] = redirect_url
        return base + "?" + urlencode(params)

    # ─── Session Endpoints ─────────────────────────────────────────────

    def create_session(self, session: MTMSession) -> dict:
        """Create a new session/talk in MeetToMatch."""
        payload = {
            "title": session.title,
            "date": session.date,
            "start": session.start,
            "end": session.end,
            "status": session.status,
            "location_type": session.location_type,
        }
        if session.source_id:
            payload["source_id"] = session.source_id
        if session.info:
            payload["info"] = session.info
        if session.type:
            payload["type"] = session.type
        if session.location:
            payload["location"] = session.location
        if session.topics:
            payload["topics"] = session.topics
        if session.groups:
            payload["groups"] = session.groups
        if session.max_visitors is not None:
            payload["max_visitors"] = session.max_visitors
        if session.speakers:
            payload["speakers"] = session.speakers
        if session.speakers_by_source_id:
            payload["speakersBySourceId"] = session.speakers_by_source_id

        logger.info(f"Creating MeetToMatch session: {session.title} (source_id={session.source_id})")
        return self._request("sessions/create", payload)

    def get_session(self, source_id: str) -> dict | None:
        """Look up a session by source_id. Returns None if not found."""
        try:
            return self._request("sessions/details", {"source_id": source_id})
        except MeetToMatchNotFoundError:
            return None

    def update_session(self, source_id: str, **fields) -> dict:
        """Update a session by source_id."""
        payload = {"source_id": source_id, **fields}
        logger.info(f"Updating MeetToMatch session: source_id={source_id}")
        return self._request("sessions/update", payload)

    def delete_session(self, source_id: str) -> dict:
        """Delete a session by source_id."""
        logger.info(f"Deleting MeetToMatch session: source_id={source_id}")
        return self._request("sessions/delete", {"source_id": source_id})

    def list_sessions(self, **filters) -> dict:
        """List sessions with optional filters (date, groups, etc.)."""
        return self._request("sessions/get", filters)

    # ─── Speaker Endpoints ─────────────────────────────────────────────

    def create_speaker(self, speaker: MTMSpeaker) -> dict:
        """Create a new speaker in MeetToMatch."""
        payload = {
            "firstname": speaker.firstname,
            "lastname": speaker.lastname,
            "priority": speaker.priority,
            "status": speaker.status,
        }
        if speaker.source_id:
            payload["source_id"] = speaker.source_id
        if speaker.email:
            payload["email"] = speaker.email
        if speaker.title:
            payload["title"] = speaker.title
        if speaker.company_name:
            payload["company_name"] = speaker.company_name
        if speaker.info:
            payload["info"] = speaker.info
        if speaker.bio:
            payload["bio"] = speaker.bio

        logger.info(f"Creating MeetToMatch speaker: {speaker.firstname} {speaker.lastname}")
        return self._request("speakers/create", payload)

    def get_speaker(self, source_id: str) -> dict | None:
        """Look up a speaker by source_id. Returns None if not found."""
        try:
            return self._request("speakers/details", {"source_id": source_id})
        except MeetToMatchNotFoundError:
            return None

    def update_speaker(self, source_id: str, **fields) -> dict:
        """Update a speaker by source_id."""
        payload = {"source_id": source_id, **fields}
        logger.info(f"Updating MeetToMatch speaker: source_id={source_id}")
        return self._request("speakers/update", payload)

    def delete_speaker(self, source_id: str) -> dict:
        """Delete a speaker by source_id."""
        logger.info(f"Deleting MeetToMatch speaker: source_id={source_id}")
        return self._request("speakers/delete", {"source_id": source_id})
