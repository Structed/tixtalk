"""Dataclass models for MeetToMatch API requests and responses."""

from dataclasses import dataclass, field


@dataclass
class MTMUser:
    """Represents a MeetToMatch user/attendee."""

    email: str
    firstname: str
    lastname: str
    company_name: str
    id: int | None = None
    group_id: int = 1
    visible: int = 1
    active: int = 1
    function: str = ""
    profile: str = ""
    web: str = ""
    locale: str = "en_US"


@dataclass
class MTMSession:
    """Represents a MeetToMatch session/talk."""

    title: str
    date: str  # YYYY-MM-DD
    start: str  # HH:MM
    end: str  # HH:MM
    source_id: str = ""
    info: str = ""
    type: str = ""
    location: str = ""
    topics: list[str] = field(default_factory=list)
    groups: list[str] = field(default_factory=list)
    max_visitors: int | None = None
    status: str = "1"
    speakers: list[int] = field(default_factory=list)
    speakers_by_source_id: list[str] = field(default_factory=list)
    location_type: str = "onsite"
    session_id: int | None = None


@dataclass
class MTMSpeaker:
    """Represents a MeetToMatch speaker."""

    firstname: str
    lastname: str
    priority: int = 1
    source_id: str = ""
    email: str = ""
    title: str = ""
    company_name: str = ""
    info: str = ""
    bio: str = ""
    status: str = "1"
    speaker_id: int | None = None
