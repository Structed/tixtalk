from django.utils.translation import gettext_lazy as _

try:
    from pretalx.common.plugins import PluginConfig
except ImportError:
    raise RuntimeError("This plugin requires pretalx to be installed.")


class PretalxPluginMeta:
    name = _("MeetToMatch Schedule Sync")
    author = "GodotFest"
    description = _(
        "Syncs the event schedule and speakers to MeetToMatch when the "
        "schedule is published or updated."
    )
    visible = True
    version = "0.1.0"


class MeetToMatchPlugin(PluginConfig):
    name = "pretalx_meettomatch"
    verbose_name = _("MeetToMatch Schedule Sync")

    class PretalxPluginMeta:
        name = _("MeetToMatch Schedule Sync")
        author = "GodotFest"
        description = _(
            "Syncs the event schedule and speakers to MeetToMatch when the "
            "schedule is published or updated."
        )
        visible = True
        version = "0.1.0"

    def ready(self):
        from . import signal_handlers  # noqa: F401


default_app_config = "pretalx_meettomatch.MeetToMatchPlugin"
