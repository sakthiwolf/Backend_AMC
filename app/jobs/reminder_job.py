"""Daily WhatsApp reminder job.

Scheduled with APScheduler at 09:00 Asia/Kolkata. Finds plans whose NotificationDate
is today for customers who have not opted out, sends the template, and logs each send
in Reminders. Idempotent: never sends twice for the same plan and cycle.
See CLAUDE.md section 4.5.
"""


def run_daily_reminders() -> None:
    """Run one pass of the daily reminder job."""
    # TODO: implement once the Plans and Reminders models exist.
    raise NotImplementedError
