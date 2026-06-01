"""Plugin settings form for the pretix-meettomatch plugin.

Provides an admin UI for configuring MeetToMatch API credentials,
product mappings, and tier settings.
"""

from django import forms
from django.utils.translation import gettext_lazy as _

from pretix.base.forms import SettingsForm


class MeetToMatchSettingsForm(SettingsForm):
    meettomatch_api_key = forms.CharField(
        label=_("MeetToMatch API Key"),
        help_text=_("Private API key provided by MeetToMatch."),
        required=True,
        widget=forms.PasswordInput(render_value=True),
    )
    meettomatch_event_id = forms.CharField(
        label=_("MeetToMatch Event ID"),
        help_text=_("Event identifier provided by MeetToMatch."),
        required=True,
    )
    meettomatch_base_url = forms.URLField(
        label=_("MeetToMatch API Base URL"),
        help_text=_("Base URL for the MeetToMatch API."),
        required=False,
        initial="https://my.meettomatch.com/public/api",
    )

    # Tier configuration
    meettomatch_full_product_ids = forms.CharField(
        label=_("Full Networking Product/Variation IDs"),
        help_text=_(
            "Comma-separated list of Pretix product or variation IDs that trigger "
            "a full MeetToMatch networking account."
        ),
        required=False,
    )
    meettomatch_addon_product_ids = forms.CharField(
        label=_("MeetToMatch Add-on Product IDs"),
        help_text=_(
            "Comma-separated list of add-on product IDs that trigger a full networking account."
        ),
        required=False,
    )
    meettomatch_full_group_id = forms.IntegerField(
        label=_("Full Networking Group ID"),
        help_text=_("MeetToMatch group ID for attendees with full networking access."),
        required=True,
        initial=1,
    )

    meettomatch_checkbox_question_id = forms.CharField(
        label=_("Opt-in Checkbox Question ID"),
        help_text=_(
            "Pretix question ID for the 'Create MeetToMatch schedule account?' checkbox. "
            "Leave empty to disable opt-in."
        ),
        required=False,
    )
    meettomatch_schedule_group_id = forms.IntegerField(
        label=_("Schedule Viewer Group ID"),
        help_text=_("MeetToMatch group ID for schedule-only attendees (no meeting access)."),
        required=False,
        initial=2,
    )

    # Company name
    meettomatch_company_question_id = forms.CharField(
        label=_("Company Name Question ID"),
        help_text=_("Pretix question ID that collects the attendee's company name. Optional."),
        required=False,
    )
    meettomatch_default_company = forms.CharField(
        label=_("Default Company Name"),
        help_text=_("Used when no company name is provided by the attendee."),
        required=False,
        initial="Independent",
    )

    # SSO
    meettomatch_sso_redirect_url = forms.URLField(
        label=_("SSO Redirect URL"),
        help_text=_("Where to redirect after SSO login (e.g., the MeetToMatch event page)."),
        required=False,
    )
