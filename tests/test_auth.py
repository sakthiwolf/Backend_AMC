"""Auth flow tests: login, lockout, refresh rotation, reuse, logout, guards."""

from datetime import datetime, timedelta, timezone

import jwt
from fastapi.testclient import TestClient
from sqlalchemy import select

from app.config import get_settings
from app.models.user import User, UserDevice
from tests.conftest import FP_A, FP_B, PASSWORD

pytestmark = __import__("pytest").mark.usefixtures("users")


def login(client: TestClient, username="alice", password=PASSWORD, fp=FP_A):
    return client.post(
        "/api/auth/login",
        json={"username": username, "password": password, "deviceFingerprint": fp},
    )


def refresh(client: TestClient, token: str, fp=FP_A):
    return client.post(
        "/api/auth/refresh", json={"deviceFingerprint": fp, "refreshToken": token}
    )


def bearer(token: str) -> dict[str, str]:
    return {"Authorization": f"Bearer {token}"}


def signed(claims: dict) -> str:
    return jwt.encode(claims, get_settings().JWT_SECRET, algorithm="HS256")


def test_login_returns_tokens(client):
    r = login(client)
    assert r.status_code == 200
    body = r.json()
    assert body["tokenType"] == "bearer"
    assert body["expiresIn"] == 8 * 3600
    assert body["user"] == {"userId": 1, "username": "alice", "fullName": "Alice A"}
    assert len(body["refreshToken"]) >= 64  # 48 random bytes, base64url
    me = client.get("/api/auth/me", headers=bearer(body["accessToken"]))
    assert me.status_code == 200
    assert me.json()["username"] == "alice"


def test_refresh_token_stored_only_as_hash(client, session_factory):
    token = login(client).json()["refreshToken"]
    with session_factory() as db:
        device = db.scalar(select(UserDevice))
        assert device.refresh_token_hash != token
        assert len(device.refresh_token_hash) == 64


def test_wrong_password_and_unknown_user_look_identical(client):
    a = login(client, password="nope-nope-nope")
    b = login(client, username="ghost")
    assert a.status_code == b.status_code == 401
    assert a.json() == b.json()
    assert a.json()["error"]["code"] == "invalid_credentials"


def test_five_wrong_passwords_lock_account(client):
    for _ in range(5):
        assert login(client, password="wrong-wrong").status_code == 401
    locked = login(client)  # correct password, but locked
    assert locked.status_code == 423
    assert locked.json()["error"]["code"] == "account_locked"


def test_lock_expires_after_15_minutes(client, session_factory):
    for _ in range(5):
        login(client, password="wrong-wrong")
    with session_factory() as db:
        user = db.scalar(select(User).where(User.username == "alice"))
        user.locked_until = datetime.now(timezone.utc).replace(tzinfo=None) - timedelta(seconds=1)
        db.commit()
    assert login(client).status_code == 200


def test_refresh_rotates_and_old_token_stops_working(client):
    first = login(client).json()
    second = refresh(client, first["refreshToken"])
    assert second.status_code == 200
    assert second.json()["refreshToken"] != first["refreshToken"]
    assert refresh(client, second.json()["refreshToken"]).status_code == 200
    stale = refresh(client, first["refreshToken"])
    assert stale.status_code == 401
    assert stale.json()["error"]["code"] == "invalid_refresh"


def test_refresh_extends_expiry(client, session_factory):
    token = login(client).json()["refreshToken"]
    with session_factory() as db:
        device = db.scalar(select(UserDevice))
        device.refresh_expires_at = datetime.now(timezone.utc).replace(tzinfo=None) + timedelta(days=1)
        db.commit()
    assert refresh(client, token).status_code == 200
    with session_factory() as db:
        expires = db.scalar(select(UserDevice)).refresh_expires_at
    assert expires > datetime.now(timezone.utc).replace(tzinfo=None) + timedelta(days=89)


def test_expired_refresh_token_rejected(client, session_factory):
    token = login(client).json()["refreshToken"]
    with session_factory() as db:
        device = db.scalar(select(UserDevice))
        device.refresh_expires_at = datetime.now(timezone.utc).replace(tzinfo=None) - timedelta(seconds=1)
        db.commit()
    assert refresh(client, token).status_code == 401


def test_reusing_old_refresh_token_revokes_device(client):
    first = login(client).json()
    second = refresh(client, first["refreshToken"]).json()
    assert refresh(client, first["refreshToken"]).status_code == 401  # replay
    # The newest token is dead too: the whole device is revoked.
    assert refresh(client, second["refreshToken"]).status_code == 401


def test_logout_blocks_refresh(client):
    tokens = login(client).json()
    r = client.post(
        "/api/auth/logout",
        json={"deviceFingerprint": FP_A},
        headers=bearer(tokens["accessToken"]),
    )
    assert r.status_code == 204
    assert refresh(client, tokens["refreshToken"]).status_code == 401


def test_login_after_logout_reactivates_device(client):
    tokens = login(client).json()
    client.post(
        "/api/auth/logout",
        json={"deviceFingerprint": FP_A},
        headers=bearer(tokens["accessToken"]),
    )
    again = login(client).json()
    assert refresh(client, again["refreshToken"]).status_code == 200


def test_logout_requires_access_token(client):
    r = client.post("/api/auth/logout", json={"deviceFingerprint": FP_A})
    assert r.status_code == 401


def test_protected_route_rejects_bad_tokens(client):
    now = datetime.now(timezone.utc)
    cases = {
        "missing": {},
        "malformed": bearer("not.a.jwt"),
        "expired": bearer(
            signed({"sub": "1", "typ": "access", "exp": now - timedelta(minutes=1)})
        ),
        "wrong_type": bearer(
            signed({"sub": "1", "typ": "refresh", "exp": now + timedelta(hours=1)})
        ),
        "bad_signature": bearer(
            jwt.encode(
                {"sub": "1", "typ": "access", "exp": now + timedelta(hours=1)},
                "some-other-secret-key-0123456789abcdef",
                algorithm="HS256",
            )
        ),
    }
    for name, headers in cases.items():
        r = client.get("/api/auth/me", headers=headers)
        assert r.status_code == 401, name
        assert set(r.json()["error"]) == {"code", "message"}, name
    expired = client.get("/api/auth/me", headers=cases["expired"])
    assert expired.json()["error"]["code"] == "token_expired"


def test_fingerprint_bound_to_one_user(client):
    assert login(client, "alice", fp=FP_A).status_code == 200
    r = login(client, "bob", fp=FP_A)
    assert r.status_code == 409
    assert login(client, "bob", fp=FP_B).status_code == 200


def test_validation_error_uses_standard_shape(client):
    r = client.post("/api/auth/login", json={"username": "alice"})
    assert r.status_code == 422
    assert r.json()["error"]["code"] == "validation_error"
