"""Manual trigger for the reminder job (admin use)."""

from fastapi import APIRouter

router = APIRouter(prefix="/api/reminders", tags=["Reminders"])

# TODO: add endpoints (see CLAUDE.md section 6).
