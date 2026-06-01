"""Django management command for manual MeetToMatch schedule sync."""

from django.core.management.base import BaseCommand, CommandError

from pretalx.event.models import Event

from ...sync import sync_schedule_to_meettomatch


class Command(BaseCommand):
    help = "Manually sync the published schedule to MeetToMatch"

    def add_arguments(self, parser):
        parser.add_argument(
            "event_slug",
            type=str,
            help="The slug of the event to sync",
        )

    def handle(self, *args, **options):
        slug = options["event_slug"]

        try:
            event = Event.objects.get(slug=slug)
        except Event.DoesNotExist:
            raise CommandError(f"Event '{slug}' not found")

        api_key = event.settings.get("meettomatch_api_key", "")
        if not api_key:
            raise CommandError(f"MeetToMatch not configured for event '{slug}'")

        # Get the current released schedule
        schedule = event.current_schedule
        if not schedule:
            raise CommandError(f"No published schedule found for event '{slug}'")

        self.stdout.write(f"Syncing schedule for '{slug}' to MeetToMatch...")

        try:
            sync_schedule_to_meettomatch(event, schedule)
            self.stdout.write(self.style.SUCCESS("Schedule sync complete!"))
        except Exception as e:
            raise CommandError(f"Sync failed: {e}")
