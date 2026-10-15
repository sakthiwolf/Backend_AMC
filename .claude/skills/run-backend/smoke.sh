#!/usr/bin/env bash
# Smoke driver for the AMC backend. Run from backend/ (Git Bash on Windows).
# Usage: smoke.sh [METHOD PATH [BEARER_TOKEN]]   e.g. smoke.sh GET /api/customers
set -u
PY=.venv/Scripts/python.exe; [ -x "$PY" ] || PY=.venv/bin/python
PORT=${PORT:-8000}; BASE=http://127.0.0.1:$PORT
LOG="${TMPDIR:-${TEMP:-/tmp}}/amc-uvicorn.log"

"$PY" -m uvicorn app.main:app --port "$PORT" >"$LOG" 2>&1 &
PID=$!
cleanup() { taskkill //PID "$PID" //F //T >/dev/null 2>&1 || kill "$PID" 2>/dev/null; }
trap cleanup EXIT

for i in $(seq 1 30); do
  curl -sf "$BASE/api/health" >/dev/null && break
  sleep 0.5
  [ "$i" = 30 ] && { echo "server did not start; log:"; cat "$LOG"; exit 1; }
done

echo "health : $(curl -s "$BASE/api/health")"
echo "routes : $(curl -s "$BASE/openapi.json" | "$PY" -c 'import sys,json; print(sorted(json.load(sys.stdin)["paths"]))')"
echo "db     : $("$PY" -c 'from sqlalchemy import text; from app.db import engine
c=engine.connect(); print(c.execute(text("select 1")).scalar()==1 and "ok")' 2>&1 | tail -1)"
if [ $# -ge 2 ]; then
  H=(); [ $# -ge 3 ] && H=(-H "Authorization: Bearer $3")
  echo "$1 $2 -> $(curl -s -w ' [%{http_code}]' -X "$1" "${H[@]}" "$BASE$2")"
fi
