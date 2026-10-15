"""Create the staff login. Usage: python -m scripts.create_user <username> [full name]

Reads the password from AMC_NEW_PASSWORD if set, otherwise prompts for it.
"""

import getpass
import os
import sys

from app.db import SessionLocal
from app.models.user import User
from app.security import hash_password


def main() -> None:
    if len(sys.argv) < 2:
        sys.exit("Usage: python -m scripts.create_user <username> [full name]")
    username = sys.argv[1]
    full_name = " ".join(sys.argv[2:]) or None
    password = os.environ.get("AMC_NEW_PASSWORD")
    if password is None:
        # Nothing is shown while typing; that is normal for getpass.
        password = getpass.getpass("Password (hidden while typing): ")
        if password != getpass.getpass("Repeat password: "):
            sys.exit("Passwords do not match.")
    if len(password) < 8:
        sys.exit("Password must be at least 8 characters.")
    with SessionLocal() as db:
        db.add(User(username=username, full_name=full_name, password_hash=hash_password(password)))
        db.commit()
    print(f"Created user {username}.")


if __name__ == "__main__":
    main()
