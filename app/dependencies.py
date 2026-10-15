"""Shared FastAPI dependencies."""

from typing import Annotated

import jwt
from fastapi import Depends
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer
from sqlalchemy.orm import Session

from app.db import get_db
from app.errors import ApiError
from app.models.user import User
from app.security import decode_access_token

_bearer = HTTPBearer(auto_error=False)

DbSession = Annotated[Session, Depends(get_db)]


def get_current_user(
    credentials: Annotated[HTTPAuthorizationCredentials | None, Depends(_bearer)],
    db: DbSession,
) -> User:
    """Resolve the access token to a user, or raise 401 in the standard shape."""
    if credentials is None:
        raise ApiError(401, "not_authenticated", "Sign in to continue.")
    try:
        claims = decode_access_token(credentials.credentials)
    except jwt.ExpiredSignatureError:
        raise ApiError(401, "token_expired", "Your session has expired.") from None
    except jwt.InvalidTokenError:
        raise ApiError(401, "invalid_token", "Invalid session token.") from None

    try:
        user = db.get(User, int(claims["sub"]))
    except ValueError:
        user = None
    if user is None:
        raise ApiError(401, "invalid_token", "Invalid session token.")
    return user


CurrentUser = Annotated[User, Depends(get_current_user)]
