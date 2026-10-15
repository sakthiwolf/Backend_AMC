"""Request and response models for /api/auth. JSON uses camelCase keys."""

from pydantic import BaseModel, ConfigDict, Field
from pydantic.alias_generators import to_camel


class CamelModel(BaseModel):
    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class LoginRequest(CamelModel):
    username: str = Field(min_length=1, max_length=50)
    password: str = Field(min_length=1, max_length=200)
    device_fingerprint: str = Field(min_length=16, max_length=100)
    device_label: str | None = Field(default=None, max_length=100)


class RefreshRequest(CamelModel):
    device_fingerprint: str = Field(min_length=16, max_length=100)
    refresh_token: str = Field(min_length=1, max_length=200)


class LogoutRequest(CamelModel):
    device_fingerprint: str = Field(min_length=16, max_length=100)


class UserOut(CamelModel):
    user_id: int
    username: str
    full_name: str


class TokenResponse(CamelModel):
    access_token: str
    token_type: str = "bearer"
    expires_in: int  # seconds
    refresh_token: str
    user: UserOut
