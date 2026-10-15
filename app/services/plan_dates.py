"""Plan date calculations.

Derives EndDate (StartDate + 12 months) and NotificationDate (StartDate + 11 months)
using calendar-month arithmetic (dateutil.relativedelta), clamping to month end.
See CLAUDE.md sections 4.2 and 4.3.
"""
