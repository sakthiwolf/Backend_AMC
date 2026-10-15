"""Users and their registered devices."""

from datetime import datetime, timezone

from sqlalchemy import Boolean, DateTime, ForeignKey, Integer, String, text
from sqlalchemy.orm import Mapped, mapped_column, relationship

from app.db import Base


def utcnow() -> datetime:
    """Naive UTC, matching how SQL Server DATETIME2 values round-trip."""
    return datetime.now(timezone.utc).replace(tzinfo=None)


class User(Base):
    __tablename__ = "Users"

    user_id: Mapped[int] = mapped_column("UserId", Integer, primary_key=True)
    username: Mapped[str] = mapped_column("Username", String(50), unique=True)
    password_hash: Mapped[str] = mapped_column("PasswordHash", String(255))
    full_name: Mapped[str | None] = mapped_column("FullName", String(100), nullable=True)
    failed_login_count: Mapped[int] = mapped_column(
        "FailedLoginCount", Integer, default=0, server_default="0"
    )
    locked_until: Mapped[datetime | None] = mapped_column(
        "LockedUntil", DateTime, nullable=True
    )
    is_active: Mapped[bool] = mapped_column(
        "IsActive", Boolean, default=True, server_default=text("1")
    )
    last_login_at: Mapped[datetime | None] = mapped_column(
        "LastLoginAt", DateTime, nullable=True
    )
    created_at: Mapped[datetime] = mapped_column(
        "CreatedAt", DateTime, default=utcnow
    )

    devices: Mapped[list["UserDevice"]] = relationship(back_populates="user")


class UserDevice(Base):
    """A phone bound to a user. Holds the hash of its current refresh token."""

    __tablename__ = "UserDevices"

    device_id: Mapped[int] = mapped_column("DeviceId", Integer, primary_key=True)
    user_id: Mapped[int] = mapped_column("UserId", ForeignKey("Users.UserId"))
    device_fingerprint: Mapped[str] = mapped_column(
        "DeviceFingerprint", String(100), unique=True
    )
    device_label: Mapped[str | None] = mapped_column(
        "DeviceLabel", String(100), nullable=True
    )
    refresh_token_hash: Mapped[str] = mapped_column("RefreshTokenHash", String(64))
    refresh_expires_at: Mapped[datetime] = mapped_column("RefreshExpiresAt", DateTime)
    created_at: Mapped[datetime] = mapped_column(
        "CreatedAt", DateTime, default=utcnow
    )
    last_used_at: Mapped[datetime | None] = mapped_column(
        "LastUsedAt", DateTime, nullable=True
    )
    revoked_at: Mapped[datetime | None] = mapped_column(
        "RevokedAt", DateTime, nullable=True
    )

    user: Mapped[User] = relationship(back_populates="devices")
