"""Login, refresh-token rotation, and logout rules. No HTTP concerns here."""

from datetime import datetime, timedelta, timezone

from sqlalchemy import select
from sqlalchemy.orm import Session

from app.config import get_settings
from app.errors import ApiError
from app.models.user import User, UserDevice
from app.security import (
    create_access_token,
    generate_refresh_token,
    hash_password,
    hash_refresh_token,
    refresh_token_matches,
    verify_password,
)

# Verified against when the username is unknown, so response time does not
# reveal which usernames exist.
_DUMMY_HASH = hash_password("not-a-real-password")


def _invalid_credentials() -> ApiError:
    return ApiError(401, "invalid_credentials", "Wrong username or password.")


def _now() -> datetime:
    """Naive UTC, matching how SQL Server DATETIME2 values round-trip."""
    return datetime.now(timezone.utc).replace(tzinfo=None)


class IssuedTokens:
    def __init__(self, user: User, access_token: str, refresh_token: str) -> None:
        self.user = user
        self.access_token = access_token
        self.refresh_token = refresh_token
        self.expires_in = get_settings().JWT_EXPIRE_HOURS * 3600


def _issue(user: User, device: UserDevice, db: Session) -> IssuedTokens:
    """Rotate the device's refresh token (sliding expiry) and mint an access token."""
    now = _now()
    refresh_token = generate_refresh_token()
    device.refresh_token_hash = hash_refresh_token(refresh_token)
    device.refresh_expires_at = now + timedelta(days=get_settings().REFRESH_TOKEN_DAYS)
    device.last_used_at = now
    device.revoked_at = None
    db.commit()
    return IssuedTokens(user, create_access_token(str(user.user_id)), refresh_token)


def login(
    db: Session,
    username: str,
    password: str,
    device_fingerprint: str,
    device_label: str | None,
) -> IssuedTokens:
    settings = get_settings()
    now = _now()
    user = db.scalar(select(User).where(User.username == username))

    if user is None or not user.is_active:
        verify_password(password, _DUMMY_HASH)
        raise _invalid_credentials()

    if user.locked_until is not None:
        if user.locked_until > now:
            raise ApiError(
                423,
                "account_locked",
                f"Too many failed attempts. Try again in {settings.LOCKOUT_MINUTES} minutes.",
            )
        # The lock has expired: start counting afresh.
        user.locked_until = None
        user.failed_login_count = 0

    if not verify_password(password, user.password_hash):
        user.failed_login_count += 1
        if user.failed_login_count >= settings.MAX_FAILED_LOGINS:
            user.locked_until = now + timedelta(minutes=settings.LOCKOUT_MINUTES)
            user.failed_login_count = 0
        db.commit()
        raise _invalid_credentials()

    user.failed_login_count = 0
    user.locked_until = None
    user.last_login_at = now

    device = db.scalar(
        select(UserDevice).where(UserDevice.device_fingerprint == device_fingerprint)
    )
    if device is not None and device.user_id != user.user_id:
        db.rollback()
        raise ApiError(
            409, "device_in_use", "This device is registered to a different user."
        )
    if device is None:
        device = UserDevice(
            user_id=user.user_id,
            device_fingerprint=device_fingerprint,
            refresh_token_hash="",
            refresh_expires_at=now,
        )
        db.add(device)
    if device_label:
        device.device_label = device_label
    return _issue(user, device, db)


def refresh(db: Session, device_fingerprint: str, refresh_token: str) -> IssuedTokens:
    invalid = ApiError(401, "invalid_refresh", "Please sign in again.")
    device = db.scalar(
        select(UserDevice).where(UserDevice.device_fingerprint == device_fingerprint)
    )
    if device is None or device.revoked_at is not None:
        raise invalid

    if not refresh_token_matches(refresh_token, device.refresh_token_hash):
        # An old token was replayed: assume it leaked and revoke this device.
        device.revoked_at = _now()
        db.commit()
        raise invalid

    if device.refresh_expires_at <= _now():
        raise invalid

    return _issue(device.user, device, db)


def logout(db: Session, user: User, device_fingerprint: str) -> None:
    """Revoke the user's device. Idempotent: an unknown device is not an error."""
    device = db.scalar(
        select(UserDevice).where(
            UserDevice.device_fingerprint == device_fingerprint,
            UserDevice.user_id == user.user_id,
        )
    )
    if device is not None and device.revoked_at is None:
        device.revoked_at = _now()
        db.commit()
