"""Auth: users and user devices.

Revision ID: 0001
Revises:
"""

import sqlalchemy as sa
from alembic import op

revision = "0001"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    # The AMC database already had a Users table (made outside Alembic) with
    # compatible columns, so only create it on a fresh database.
    if not sa.inspect(op.get_bind()).has_table("Users"):
        _create_users()
    _create_user_devices()


def _create_users() -> None:
    op.create_table(
        "Users",
        sa.Column("UserId", sa.Integer(), sa.Identity(), primary_key=True),
        sa.Column("Username", sa.String(50), nullable=False, unique=True),
        sa.Column("PasswordHash", sa.String(255), nullable=False),
        sa.Column("FullName", sa.String(100), nullable=True),
        sa.Column("FailedLoginCount", sa.Integer(), nullable=False, server_default="0"),
        sa.Column("LockedUntil", sa.DateTime(), nullable=True),
        sa.Column("IsActive", sa.Boolean(), nullable=False, server_default="1"),
        sa.Column("LastLoginAt", sa.DateTime(), nullable=True),
        sa.Column(
            "CreatedAt",
            sa.DateTime(),
            nullable=False,
            server_default=sa.text("SYSUTCDATETIME()"),
        ),
    )


def _create_user_devices() -> None:
    op.create_table(
        "UserDevices",
        sa.Column("DeviceId", sa.Integer(), sa.Identity(), primary_key=True),
        sa.Column("UserId", sa.Integer(), sa.ForeignKey("Users.UserId"), nullable=False),
        sa.Column("DeviceFingerprint", sa.String(100), nullable=False, unique=True),
        sa.Column("DeviceLabel", sa.String(100), nullable=True),
        sa.Column("RefreshTokenHash", sa.String(64), nullable=False),
        sa.Column("RefreshExpiresAt", sa.DateTime(), nullable=False),
        sa.Column(
            "CreatedAt",
            sa.DateTime(),
            nullable=False,
            server_default=sa.text("SYSUTCDATETIME()"),
        ),
        sa.Column("LastUsedAt", sa.DateTime(), nullable=True),
        sa.Column("RevokedAt", sa.DateTime(), nullable=True),
    )
    op.create_index("IX_UserDevices_UserId", "UserDevices", ["UserId"])


def downgrade() -> None:
    op.drop_index("IX_UserDevices_UserId", table_name="UserDevices")
    # Users is left in place: it may predate this migration.
    op.drop_table("UserDevices")
