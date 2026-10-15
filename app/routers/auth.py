"""Login, refresh, logout, and current-user endpoints. HTTP only."""

from fastapi import APIRouter, Response, status

from app.dependencies import CurrentUser, DbSession
from app.models.user import User
from app.schemas.auth import (
    LoginRequest,
    LogoutRequest,
    RefreshRequest,
    TokenResponse,
    UserOut,
)
from app.services import auth as auth_service

router = APIRouter(prefix="/api/auth", tags=["Auth"])


def _user_out(user: User) -> UserOut:
    return UserOut(
        user_id=user.user_id,
        username=user.username,
        full_name=user.full_name or user.username,
    )


def _token_response(issued: auth_service.IssuedTokens) -> TokenResponse:
    return TokenResponse(
        access_token=issued.access_token,
        expires_in=issued.expires_in,
        refresh_token=issued.refresh_token,
        user=_user_out(issued.user),
    )


@router.post("/login", response_model=TokenResponse, response_model_by_alias=True)
def login(body: LoginRequest, db: DbSession) -> TokenResponse:
    return _token_response(
        auth_service.login(
            db, body.username, body.password, body.device_fingerprint, body.device_label
        )
    )


@router.post("/refresh", response_model=TokenResponse, response_model_by_alias=True)
def refresh(body: RefreshRequest, db: DbSession) -> TokenResponse:
    return _token_response(
        auth_service.refresh(db, body.device_fingerprint, body.refresh_token)
    )


@router.post("/logout", status_code=status.HTTP_204_NO_CONTENT)
def logout(body: LogoutRequest, user: CurrentUser, db: DbSession) -> Response:
    auth_service.logout(db, user, body.device_fingerprint)
    return Response(status_code=status.HTTP_204_NO_CONTENT)


@router.get("/me", response_model=UserOut, response_model_by_alias=True)
def me(user: CurrentUser) -> UserOut:
    return _user_out(user)
