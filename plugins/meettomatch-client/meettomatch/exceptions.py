"""Custom exceptions for the MeetToMatch API client."""


class MeetToMatchError(Exception):
    """Base exception for MeetToMatch client errors."""

    pass


class MeetToMatchAPIError(MeetToMatchError):
    """Raised when the MeetToMatch API returns an error response."""

    def __init__(self, message: str, status_code: int | None = None, response_data: dict | None = None):
        self.status_code = status_code
        self.response_data = response_data or {}
        super().__init__(message)


class MeetToMatchAuthError(MeetToMatchAPIError):
    """Raised when authentication fails (PERMISSION_DENIED)."""

    pass


class MeetToMatchNotFoundError(MeetToMatchAPIError):
    """Raised when a requested resource is not found."""

    pass
