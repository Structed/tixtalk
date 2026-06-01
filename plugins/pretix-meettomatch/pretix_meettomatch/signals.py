"""Signal handlers for the pretix-meettomatch plugin.

Listens to order_placed, order_paid, and order_changed signals to trigger
MeetToMatch account creation/upgrades.
"""

import logging

from django.dispatch import receiver

from pretix.base.signals import order_placed, order_paid, order_changed

from .tasks import process_order_for_meettomatch

logger = logging.getLogger(__name__)


@receiver(order_placed, dispatch_uid="meettomatch_order_placed")
def on_order_placed(sender, order, **kwargs):
    """Triggered when a new order is placed (regardless of payment status)."""
    if not _plugin_enabled(sender):
        return
    logger.info(f"MeetToMatch: order_placed signal for order {order.code}")
    process_order_for_meettomatch.apply_async(args=(order.pk,))


@receiver(order_paid, dispatch_uid="meettomatch_order_paid")
def on_order_paid(sender, order, **kwargs):
    """Triggered when an order is marked as paid.

    For payment-gated flows where accounts should only be created after payment.
    """
    if not _plugin_enabled(sender):
        return
    logger.info(f"MeetToMatch: order_paid signal for order {order.code}")
    process_order_for_meettomatch.apply_async(args=(order.pk,))


@receiver(order_changed, dispatch_uid="meettomatch_order_changed")
def on_order_changed(sender, order, **kwargs):
    """Triggered when an order is modified (e.g., add-on added later)."""
    if not _plugin_enabled(sender):
        return
    logger.info(f"MeetToMatch: order_changed signal for order {order.code}")
    process_order_for_meettomatch.apply_async(args=(order.pk,))


def _plugin_enabled(event) -> bool:
    """Check if the MeetToMatch plugin is enabled for this event."""
    return event.settings.get("meettomatch_api_key", default="") != ""
