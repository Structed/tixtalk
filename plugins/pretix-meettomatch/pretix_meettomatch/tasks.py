"""Celery tasks for MeetToMatch integration.

All MeetToMatch API calls are made asynchronously via Celery to avoid
blocking the order placement flow.
"""

import logging

from django.conf import settings

from pretix.base.models import Order, OrderPosition
from pretix.celery_app import app

from .client_factory import get_mtm_client
from .logic import determine_mtm_action, create_or_upgrade_account

logger = logging.getLogger(__name__)


@app.task(bind=True, max_retries=3, default_retry_delay=60)
def process_order_for_meettomatch(self, order_pk: int):
    """Process an order to determine if MeetToMatch accounts need to be created/upgraded.

    This task:
    1. Loads the order and its positions
    2. Checks payment status (only paid orders are processed for full networking)
    3. Determines which positions qualify for MeetToMatch (full or schedule-only)
    4. Creates or upgrades accounts as needed
    5. Stores the result in order metadata
    """
    try:
        order = Order.objects.select_related("event").get(pk=order_pk)
    except Order.DoesNotExist:
        logger.error(f"Order {order_pk} not found, skipping MeetToMatch processing")
        return

    event = order.event

    # Only process paid orders (or explicitly free orders)
    if order.status not in ("p", "n"):  # p=paid, n=pending (free orders)
        logger.debug(
            f"Order {order.code} status is '{order.status}', skipping MTM processing"
        )
        return

    client = get_mtm_client(event)
    if client is None:
        logger.debug(f"MeetToMatch not configured for event {event.slug}, skipping")
        return

    positions = order.positions.select_related("item", "variation").prefetch_related("answers")

    for position in positions:
        try:
            action = determine_mtm_action(event, position)
            if action is None:
                continue

            create_or_upgrade_account(client, event, order, position, action)
        except Exception as exc:
            logger.exception(
                f"MeetToMatch: Failed to process position {position.pk} "
                f"in order {order.code}: {exc}"
            )
            raise self.retry(exc=exc)
