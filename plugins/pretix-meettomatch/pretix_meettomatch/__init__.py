from django.utils.translation import gettext_lazy as _

try:
    from pretix.base.plugins import PluginConfig
except ImportError:
    raise RuntimeError("This plugin requires pretix to be installed.")


class PretixPluginMeta:
    name = _("MeetToMatch Integration")
    author = "GodotFest"
    description = _(
        "Automatically creates MeetToMatch attendee accounts when tickets are "
        "purchased, with SSO login links in confirmation emails."
    )
    visible = True
    version = "0.1.0"
    category = "INTEGRATION"
    compatibility = "pretix>=2024.1.0"


class MeetToMatchPlugin(PluginConfig):
    name = "pretix_meettomatch"
    verbose_name = _("MeetToMatch Integration")

    class PretixPluginMeta:
        name = _("MeetToMatch Integration")
        author = "GodotFest"
        description = _(
            "Automatically creates MeetToMatch attendee accounts when tickets are "
            "purchased, with SSO login links in confirmation emails."
        )
        visible = True
        version = "0.1.0"
        category = "INTEGRATION"
        compatibility = "pretix>=2024.1.0"

    def ready(self):
        from . import signals  # noqa: F401


default_app_config = "pretix_meettomatch.MeetToMatchPlugin"
