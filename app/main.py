"""FastAPI application entry point."""

from fastapi import Depends, FastAPI
from fastapi.middleware.cors import CORSMiddleware

from app.dependencies import get_current_user
from app.errors import register_error_handlers
from app.routers import auth, customers, dashboard, plans, reminders, reports

app = FastAPI(title="AMC & Monthly Plan Tracker API", version="0.1.0")

# Development only: lets the Flutter web build (served from localhost on a random
# port) call the API. The Android app does not need CORS.
# TODO: restrict or remove for production.
app.add_middleware(
    CORSMiddleware,
    allow_origin_regex=r"http://(localhost|127\.0\.0\.1)(:\d+)?",
    allow_methods=["*"],
    allow_headers=["*"],
)

register_error_handlers(app)

# /api/auth/login and /api/auth/refresh are public; auth.router guards its own
# other routes. Every other router requires a valid access token.
app.include_router(auth.router)
_protected = [Depends(get_current_user)]
for _router in (
    customers.router,
    plans.router,
    reminders.router,
    dashboard.router,
    reports.router,
):
    app.include_router(_router, dependencies=_protected)

# TODO: start the APScheduler daily reminder job (app.jobs.reminder_job) on startup.


@app.get("/api/health", tags=["health"])
def health() -> dict[str, str]:
    return {"status": "ok"}
