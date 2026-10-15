"""Shared pytest fixtures."""

import os

# Placeholder values so app.config can load without a real .env during tests.
os.environ.setdefault("DATABASE_URL", "sqlite:///:memory:")
os.environ.setdefault("JWT_SECRET", "test-secret-not-for-production-0123456789")

from collections.abc import Generator  # noqa: E402

import pytest  # noqa: E402
from fastapi.testclient import TestClient  # noqa: E402
from sqlalchemy import create_engine  # noqa: E402
from sqlalchemy.orm import Session, sessionmaker  # noqa: E402
from sqlalchemy.pool import StaticPool  # noqa: E402

from app.db import Base, get_db  # noqa: E402
from app.main import app  # noqa: E402
from app.models.user import User  # noqa: E402
from app.security import hash_password  # noqa: E402

PASSWORD = "correct-horse-battery"
FP_A = "11111111-1111-4111-8111-111111111111" + "1" * 32
FP_B = "22222222-2222-4222-8222-222222222222" + "2" * 32


@pytest.fixture()
def session_factory() -> Generator[sessionmaker[Session], None, None]:
    engine = create_engine(
        "sqlite://", connect_args={"check_same_thread": False}, poolclass=StaticPool
    )
    Base.metadata.create_all(engine)
    yield sessionmaker(bind=engine, autoflush=False, expire_on_commit=False)
    engine.dispose()


@pytest.fixture()
def client(session_factory: sessionmaker[Session]) -> Generator[TestClient, None, None]:
    def _get_db() -> Generator[Session, None, None]:
        db = session_factory()
        try:
            yield db
        finally:
            db.close()

    app.dependency_overrides[get_db] = _get_db
    with TestClient(app) as c:
        yield c
    app.dependency_overrides.clear()


@pytest.fixture()
def users(session_factory: sessionmaker[Session]) -> None:
    with session_factory() as db:
        db.add(User(username="alice", full_name="Alice A", password_hash=hash_password(PASSWORD)))
        db.add(User(username="bob", full_name="Bob B", password_hash=hash_password(PASSWORD)))
        db.commit()
