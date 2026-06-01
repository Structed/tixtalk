"""Core business logic for MeetToMatch account creation and upgrades.

Determines which order positions qualify for MeetToMatch accounts and
what tier (full networking vs schedule-only) they should receive.
"""

import logging
from enum import Enum

from meettomatch import MeetToMatchClient
from meettomatch.models import MTMUser

logger = logging.getLogger(__name__)


class MTMAction(Enum):
    """The type of MeetToMatch account action to take."""

    FULL_NETWORKING = "full"
    SCHEDULE_ONLY = "schedule_only"


def determine_mtm_action(event, position) -> MTMAction | None:
    """Determine what MeetToMatch action (if any) a position requires.

    Checks:
    1. Is this a "full MeetToMatch" product/variation or add-on?
    2. Does this position have the opt-in checkbox answered "yes"?

    Returns None if no MeetToMatch action is needed.
    """
    # Get configured product/variation IDs for full networking
    full_product_ids = _get_id_list(event.settings.get("meettomatch_full_product_ids", default=""))
    addon_product_ids = _get_id_list(event.settings.get("meettomatch_addon_product_ids", default=""))

    # Check if this position's item (or variation) triggers full networking
    item_id = str(position.item_id)
    variation_id = str(position.variation_id) if position.variation_id else ""

    if item_id in full_product_ids or variation_id in full_product_ids:
        return MTMAction.FULL_NETWORKING

    if item_id in addon_product_ids or variation_id in addon_product_ids:
        return MTMAction.FULL_NETWORKING

    # Check for the opt-in checkbox (schedule-only)
    checkbox_question_id = event.settings.get("meettomatch_checkbox_question_id", default="")
    if checkbox_question_id:
        for answer in position.answers.all():
            if str(answer.question_id) == checkbox_question_id and answer.answer.lower() in (
                "true",
                "yes",
                "1",
            ):
                return MTMAction.SCHEDULE_ONLY

    return None


def create_or_upgrade_account(client: MeetToMatchClient, event, order, position, action: MTMAction):
    """Create a new MeetToMatch account or upgrade an existing one.

    If the user already exists:
    - If they're schedule-only and action is FULL_NETWORKING → upgrade
    - Otherwise → skip (already provisioned)

    Stores result in order meta for tracking.
    """
    attendee_email = _get_attendee_email(position, order)
    if not attendee_email:
        logger.warning(f"No email found for position {position.pk} in order {order.code}")
        return

    attendee_name = _get_attendee_name(position, order)
    company_name = _get_company_name(event, position)

    # Check if user already exists
    existing_user = client.get_user(attendee_email)

    if existing_user:
        mtm_user_id = existing_user.get("id") or existing_user.get("data", {}).get("id")
        current_group = str(existing_user.get("group_id") or existing_user.get("data", {}).get("group_id", ""))
        schedule_group = str(event.settings.get("meettomatch_schedule_group_id", default="0"))

        # Upgrade from schedule-only to full networking
        if action == MTMAction.FULL_NETWORKING and current_group == schedule_group:
            full_group = int(event.settings.get("meettomatch_full_group_id", default="1"))
            client.update_user(attendee_email, group_id=full_group, visible=1)
            _store_meta(order, position, attendee_email, "upgraded", mtm_user_id)
            logger.info(f"Upgraded MeetToMatch user {attendee_email} to full networking")
        else:
            _store_meta(order, position, attendee_email, "already_exists", mtm_user_id)
            logger.info(f"MeetToMatch user {attendee_email} already exists, skipping")
        return

    # Create new account
    if action == MTMAction.FULL_NETWORKING:
        group_id = int(event.settings.get("meettomatch_full_group_id", default="1"))
        visible = 1
    else:
        group_id = int(event.settings.get("meettomatch_schedule_group_id", default="2"))
        visible = 0

    user = MTMUser(
        email=attendee_email,
        firstname=attendee_name[0],
        lastname=attendee_name[1],
        company_name=company_name,
        group_id=group_id,
        visible=visible,
    )

    result = client.create_user(user, send_email=False)
    mtm_user_id = result.get("id") or result.get("data", {}).get("id")
    _store_meta(order, position, attendee_email, f"created_{action.value}", mtm_user_id)
    logger.info(f"Created MeetToMatch {action.value} account for {attendee_email}")

    # Generate SSO link and store in meta per-position for email template
    try:
        redirect_url = event.settings.get("meettomatch_sso_redirect_url", default="")
        sso_url = client.build_sso_url(attendee_email, redirect_url)
        order.meta_info = order.meta_info or {}
        if isinstance(order.meta_info, str):
            import json
            order.meta_info = json.loads(order.meta_info)
        order.meta_info.setdefault("meettomatch", {})
        order.meta_info["meettomatch"][f"sso_url_{position.pk}"] = sso_url
        # Also store as primary SSO URL for single-attendee orders
        order.meta_info["meettomatch"]["sso_url"] = sso_url
        order.save(update_fields=["meta_info"])
    except Exception as e:
        logger.warning(f"Failed to generate SSO URL for {attendee_email}: {e}")


def _get_attendee_email(position, order) -> str:
    """Extract attendee email from position or fall back to order email.

    For add-on positions, resolve to the parent position's attendee.
    """
    # Add-ons: resolve to the addon_to (parent) position's attendee
    if hasattr(position, "addon_to") and position.addon_to:
        parent = position.addon_to
        if hasattr(parent, "attendee_email") and parent.attendee_email:
            return parent.attendee_email

    if hasattr(position, "attendee_email") and position.attendee_email:
        return position.attendee_email
    return order.email


def _get_attendee_name(position, order) -> tuple[str, str]:
    """Extract attendee first/last name. Falls back to order invoice address.

    For add-on positions, resolve to the parent position's attendee.
    """
    # Add-ons: resolve to the addon_to (parent) position's attendee name
    target = position
    if hasattr(position, "addon_to") and position.addon_to:
        target = position.addon_to

    if hasattr(target, "attendee_name") and target.attendee_name:
        parts = target.attendee_name.strip().rsplit(" ", 1)
        if len(parts) == 2:
            return (parts[0], parts[1])
        return (parts[0], "")

    # Fall back to invoice address
    try:
        ia = order.invoice_address
        if ia:
            return (ia.name_parts.get("given_name", ""), ia.name_parts.get("family_name", ""))
    except Exception:
        pass

    return ("Attendee", "")


def _get_company_name(event, position) -> str:
    """Get company name from answers or use configured default."""
    company_question_id = event.settings.get("meettomatch_company_question_id", default="")
    if company_question_id:
        for answer in position.answers.all():
            if str(answer.question_id) == company_question_id and answer.answer:
                return answer.answer

    return event.settings.get("meettomatch_default_company", default="Independent")


def _get_id_list(setting_value: str) -> list[str]:
    """Parse a comma-separated list of IDs from a settings string."""
    if not setting_value:
        return []
    return [s.strip() for s in setting_value.split(",") if s.strip()]


def _store_meta(order, position, email: str, status: str, mtm_user_id=None):
    """Store MeetToMatch processing result in order metadata."""
    order.meta_info = order.meta_info or {}
    if isinstance(order.meta_info, str):
        import json
        order.meta_info = json.loads(order.meta_info)

    order.meta_info.setdefault("meettomatch", {})
    order.meta_info["meettomatch"][f"position_{position.pk}"] = {
        "email": email,
        "status": status,
        "mtm_user_id": mtm_user_id,
    }
    order.save(update_fields=["meta_info"])
