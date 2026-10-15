"""Application settings, loaded from environment variables or a .env file."""

from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore",
    )

    DATABASE_URL: str
    JWT_SECRET: str
    JWT_EXPIRE_HOURS: int = 8
    REFRESH_TOKEN_DAYS: int = 90
    MAX_FAILED_LOGINS: int = 5
    LOCKOUT_MINUTES: int = 15
    WHATSAPP_TOKEN: str = ""
    WHATSAPP_PHONE_NUMBER_ID: str = ""
    TIMEZONE: str = "Asia/Kolkata"


@lru_cache
def get_settings() -> Settings:
    """Return the settings, read once and cached for the process lifetime."""
    return Settings()  # type: ignore[call-arg]
