"""Password hashing, access-token JWTs, and refresh-token helpers."""

import hashlib
import hmac
import secrets
from datetime import datetime, timedelta, timezone
from typing import Any

import jwt
from pwdlib import PasswordHash

from app.config import get_settings

ALGORITHM = "HS256"
ACCESS_TOKEN_TYPE = "access"

_hasher = PasswordHash.recommended()


def hash_password(password: str) -> str:
    """Hash a plain-text password with pwdlib (argon2)."""
    return _hasher.hash(password)


def verify_password(password: str, password_hash: str) -> bool:
    """Check a plain-text password against a stored hash."""
    return _hasher.verify(password, password_hash)


def create_access_token(subject: str) -> str:
    """Create a signed JWT for the subject, expiring after JWT_EXPIRE_HOURS."""
    settings = get_settings()
    now = datetime.now(timezone.utc)
    claims = {
        "sub": subject,
        "typ": ACCESS_TOKEN_TYPE,
        "iat": now,
        "exp": now + timedelta(hours=settings.JWT_EXPIRE_HOURS),
    }
    return jwt.encode(claims, settings.JWT_SECRET, algorithm=ALGORITHM)


def decode_access_token(token: str) -> dict[str, Any]:
    """Verify signature, expiry, and token type; return the claims.

    Raises jwt.ExpiredSignatureError for expired tokens and jwt.InvalidTokenError
    for anything else wrong (malformed, bad signature, wrong type).
    """
    claims: dict[str, Any] = jwt.decode(
        token,
        get_settings().JWT_SECRET,
        algorithms=[ALGORITHM],
        options={"require": ["exp", "sub", "typ"]},
    )
    if claims["typ"] != ACCESS_TOKEN_TYPE:
        raise jwt.InvalidTokenError("wrong token type")
    return claims


def generate_refresh_token() -> str:
    """A random 48-byte token, URL-safe encoded."""
    return secrets.token_urlsafe(48)


def hash_refresh_token(token: str) -> str:
    """SHA-256 hex digest. Refresh tokens are high-entropy, so no salt is needed."""
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def refresh_token_matches(token: str, stored_hash: str) -> bool:
    return hmac.compare_digest(hash_refresh_token(token), stored_hash)
