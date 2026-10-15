---
name: run-backend
description: Run, start, smoke-test, and curl the AMC & Monthly Plan Tracker FastAPI backend (uvicorn + SQL Server Express) on Windows. Use when asked to run, start, test, or hit the API.
---

# Run the AMC backend

FastAPI app driven with `curl`. Paths are relative to `backend/`. Shell: Git Bash (the venv is a Windows venv at `.venv/Scripts`).

State when written: only `GET /api/health` exists. The routers (auth, customers, plans, ...) are empty stubs and `app/main.py` does not start the scheduler yet. Re-check `/openapi.json` before assuming an endpoint exists.

## Prerequisites
- `.venv` already exists; `.env` already points at local SQL Server Express (`localhost\SQLEXPRESS`, DB `AMCTracker`, Windows auth, ODBC Driver 18). If `.venv` is missing: `python -m venv .venv && .venv/Scripts/python.exe -m pip install -r requirements.txt`.
- `.env` needs `DATABASE_URL` and `JWT_SECRET` (copy from `.env.example`). Never commit it.

## Run (agent path)
```bash
bash .claude/skills/run-backend/smoke.sh                       # start, check health/routes/DB, stop
bash .claude/skills/run-backend/smoke.sh GET /api/customers    # also one extra request
bash .claude/skills/run-backend/smoke.sh GET /api/customers <JWT>   # with Bearer token
```
Output (verified): `health : {"status":"ok"}`, `routes : ['/api/health']`, `db : ok`. The script starts uvicorn on 8000 (override with `PORT=`), waits for health, and kills it on exit. Server log: `$TEMP/amc-uvicorn.log`.

## Test
```bash
.venv/Scripts/python.exe -m pytest -q     # 1 passed (placeholder only for now)
```
`tests/conftest.py` sets a sqlite `DATABASE_URL`, so tests never touch SQL Server.

## Run (human path)
```bash
.venv/Scripts/python.exe -m uvicorn app.main:app --reload --port 8000
```
Swagger at http://127.0.0.1:8000/docs. Blocks the terminal.

## Gotchas
- `bash` `kill` does not stop a Windows uvicorn reliably; use `taskkill //PID <pid> //F //T` (double slash in Git Bash). With `--reload` there is a parent and child process, so `/T` is needed. Free a stuck port: `netstat -ano | grep ':8000 .*LISTENING'`.
- The SQLAlchemy engine connects lazily, so `/api/health` returns ok even if SQL Server is down; the smoke script's `db` line is the real DB check.
- CORS is open only for `localhost`/`127.0.0.1` origins (for Flutter web).
- The system Python is 3.15 beta; always use `.venv/Scripts/python.exe`.
