"""ORM models. Import every model module here so Alembic autogenerate can see it."""

from app.models.user import User, UserDevice  # noqa: F401
