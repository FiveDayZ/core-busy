#!/usr/bin/env python3
"""CORE-BUSY telemetry hub (stdlib only, no third-party deps).

环境变量:
  COREBUSY_HUB_PORT         监听端口, 默认 8400
  COREBUSY_HUB_DATA         数据目录, 默认 /data
  COREBUSY_HUB_TOKEN        管理员 Token; 缺省时首次启动自动生成到 <data>/token.txt
  COREBUSY_HUB_HISTORY_KEEP 每设备心跳流水保留条数, 默认 5000
  COREBUSY_HUB_INVITE_TTL   注册码有效期(小时), 默认 24

鉴权模型 (v1.35.0 注册制):
  管理员 Token  —— 看板/管理 API; 心跳也收(旧版设备平滑迁移)。
  设备 Token    —— 管理员生成一次性注册码, 设备 POST /api/enroll 兑换;
                   心跳用设备 Token 时强制校验载荷 deviceId 与 Token 归属一致。
"""

import json
import os
import re
import sqlite3
import threading
import uuid
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

PORT = int(os.environ.get("COREBUSY_HUB_PORT", "8400"))
DATA_DIR = os.environ.get("COREBUSY_HUB_DATA", "/data")
TOKEN_ENV = os.environ.get("COREBUSY_HUB_TOKEN", "").strip()
HISTORY_KEEP = max(10, int(os.environ.get("COREBUSY_HUB_HISTORY_KEEP", "5000")))
INVITE_TTL_HOURS = max(1, int(os.environ.get("COREBUSY_HUB_INVITE_TTL", "24")))

STATIC_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "static")
MAX_BODY = 256 * 1024
DEVICE_ID_RE = re.compile(r"^[0-9a-f]{16}$")

TEXT_LIMITS = {
    "alias": 64,
    "cpuModel": 128,
    "os": 128,
    "appVersion": 32,
}


def utc_now_iso() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="seconds")


def _load_token() -> str:
    if TOKEN_ENV:
        return TOKEN_ENV
    token_path = os.path.join(DATA_DIR, "token.txt")
    if os.path.exists(token_path):
        with open(token_path, "r", encoding="utf-8") as fh:
            token = fh.read().strip()
        if token:
            return token
    token = uuid.uuid4().hex
    with open(token_path, "w", encoding="utf-8") as fh:
        fh.write(token)
    try:
        os.chmod(token_path, 0o600)
    except OSError:
        pass
    return token


os.makedirs(DATA_DIR, exist_ok=True)
TOKEN = _load_token()

_db_lock = threading.Lock()
_conn = sqlite3.connect(os.path.join(DATA_DIR, "telemetry.db"), check_same_thread=False)
_conn.row_factory = sqlite3.Row
_conn.execute("PRAGMA journal_mode=WAL")
_conn.execute("PRAGMA busy_timeout=5000")
_conn.executescript(
    """
    CREATE TABLE IF NOT EXISTS devices (
        device_id      TEXT PRIMARY KEY,
        alias          TEXT,
        cpu_model      TEXT,
        cores_physical INTEGER,
        cores_logical  INTEGER,
        hybrid         INTEGER DEFAULT 0,
        os             TEXT,
        app_version    TEXT,
        runtime_total  REAL,
        energy_total   REAL,
        power_last     REAL,
        power_peak     REAL,
        cores_grade    TEXT,
        first_seen     TEXT NOT NULL,
        last_seen      TEXT NOT NULL
    );
    CREATE TABLE IF NOT EXISTS heartbeats (
        id           INTEGER PRIMARY KEY AUTOINCREMENT,
        device_id    TEXT NOT NULL,
        ts           TEXT NOT NULL,
        runtime_total REAL,
        energy_total  REAL,
        power_last    REAL,
        power_peak    REAL,
        cores_grade   TEXT
    );
    CREATE INDEX IF NOT EXISTS idx_heartbeats_device ON heartbeats(device_id, id);
    CREATE TABLE IF NOT EXISTS invites (
        code       TEXT PRIMARY KEY,
        created_at TEXT NOT NULL,
        expires_at TEXT NOT NULL,
        used_by    TEXT
    );
    """
)
# v1.35.0 迁移: 旧库补 api_token 列(注册制按设备 Token)。
try:
    _conn.execute("ALTER TABLE devices ADD COLUMN api_token TEXT")
except sqlite3.OperationalError:
    pass  # 列已存在
_conn.commit()


def _clamp_text(value, limit: int):
    if not isinstance(value, str):
        return None
    value = value.strip()
    return value[:limit] if value else None


def _num(value):
    """接受数字或 null; 非有限/非法一律 None(=看板显示 '-'), 不伪造 0。"""
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        value = float(value)
        return value if value == value and value not in (float("inf"), float("-inf")) else None
    return None


def _grades_text(value) -> str | None:
    if not isinstance(value, list) or not value or len(value) > 256:
        return None
    cleaned = []
    for item in value:
        if not isinstance(item, dict):
            continue
        name = _clamp_text(item.get("name"), 16)
        if not name:
            continue
        cleaned.append(
            {
                "name": name,
                "type": _clamp_text(item.get("type"), 4) or "S",
                "level": int(item["level"]) if isinstance(item.get("level"), int) else None,
                "stage": int(item["stage"]) if isinstance(item.get("stage"), int) else None,
                "score": _num(item.get("score")),
            }
        )
    if not cleaned:
        return None
    text = json.dumps(cleaned, ensure_ascii=False, separators=(",", ":"))
    return None if len(text) > 65536 else text


def handle_heartbeat(payload: dict) -> dict:
    device_id = payload.get("deviceId")
    if not isinstance(device_id, str) or not DEVICE_ID_RE.match(device_id):
        raise ValueError("invalid deviceId")
    now = utc_now_iso()
    grades = _grades_text(payload.get("coresGrade"))
    fields = {
        "alias": _clamp_text(payload.get("alias"), TEXT_LIMITS["alias"]),
        "cpu_model": _clamp_text(payload.get("cpuModel"), TEXT_LIMITS["cpuModel"]),
        "cores_physical": payload.get("coresPhysical") if isinstance(payload.get("coresPhysical"), int) else None,
        "cores_logical": payload.get("coresLogical") if isinstance(payload.get("coresLogical"), int) else None,
        "hybrid": 1 if payload.get("hybrid") is True else 0,
        "os": _clamp_text(payload.get("os"), TEXT_LIMITS["os"]),
        "app_version": _clamp_text(payload.get("appVersion"), TEXT_LIMITS["appVersion"]),
        "runtime_total": _num(payload.get("runtimeTotalSeconds")),
        "energy_total": _num(payload.get("energyTotalWh")),
        "power_last": _num(payload.get("powerLastW")),
        "power_peak": _num(payload.get("powerPeakW")),
        "cores_grade": grades,
    }
    with _db_lock:
        row = _conn.execute(
            "SELECT first_seen FROM devices WHERE device_id = ?", (device_id,)
        ).fetchone()
        first_seen = row["first_seen"] if row else now
        _conn.execute(
            """
            INSERT INTO devices (device_id, alias, cpu_model, cores_physical, cores_logical,
                                 hybrid, os, app_version, runtime_total, energy_total,
                                 power_last, power_peak, cores_grade, first_seen, last_seen)
            VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            ON CONFLICT(device_id) DO UPDATE SET
                alias=excluded.alias, cpu_model=excluded.cpu_model,
                cores_physical=excluded.cores_physical, cores_logical=excluded.cores_logical,
                hybrid=excluded.hybrid, os=excluded.os, app_version=excluded.app_version,
                runtime_total=excluded.runtime_total, energy_total=excluded.energy_total,
                power_last=excluded.power_last, power_peak=excluded.power_peak,
                cores_grade=excluded.cores_grade, last_seen=excluded.last_seen
            """,
            (device_id, fields["alias"], fields["cpu_model"], fields["cores_physical"],
             fields["cores_logical"], fields["hybrid"], fields["os"], fields["app_version"],
             fields["runtime_total"], fields["energy_total"], fields["power_last"],
             fields["power_peak"], fields["cores_grade"], first_seen, now),
        )
        _conn.execute(
            """
            INSERT INTO heartbeats (device_id, ts, runtime_total, energy_total,
                                    power_last, power_peak, cores_grade)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (device_id, now, fields["runtime_total"], fields["energy_total"],
             fields["power_last"], fields["power_peak"], fields["cores_grade"]),
        )
        _conn.execute(
            """
            DELETE FROM heartbeats WHERE device_id = ? AND id NOT IN (
                SELECT id FROM heartbeats WHERE device_id = ? ORDER BY id DESC LIMIT ?
            )
            """,
            (device_id, device_id, HISTORY_KEEP),
        )
        _conn.commit()
    return {"ok": True, "firstSeen": first_seen, "lastSeen": now}


class ApiError(Exception):
    """带 HTTP 状态码的业务错误。"""

    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status
        self.message = message


# -- 注册制 (v1.35.0) ---------------------------------------------------------

def create_invite() -> dict:
    now = datetime.now(timezone.utc)
    expires = now + timedelta(hours=INVITE_TTL_HOURS)
    code = uuid.uuid4().hex[:8].upper()
    row = {
        "code": code,
        "created_at": now.isoformat(timespec="seconds"),
        "expires_at": expires.isoformat(timespec="seconds"),
    }
    with _db_lock:
        _conn.execute(
            "INSERT INTO invites (code, created_at, expires_at, used_by) VALUES (?, ?, ?, NULL)",
            (row["code"], row["created_at"], row["expires_at"]),
        )
        _conn.commit()
    return {"code": code, "createdAt": row["created_at"], "expiresAt": row["expires_at"]}


def list_invites() -> list[dict]:
    with _db_lock:
        rows = _conn.execute(
            "SELECT code, created_at, expires_at, used_by FROM invites ORDER BY created_at DESC"
        ).fetchall()
    now = utc_now_iso()
    result = []
    for r in rows:
        result.append(
            {
                "code": r["code"],
                "createdAt": r["created_at"],
                "expiresAt": r["expires_at"],
                "usedBy": r["used_by"],
                "status": "used" if r["used_by"] else ("expired" if r["expires_at"] < now else "active"),
            }
        )
    return result


def delete_invite(code: str) -> bool:
    with _db_lock:
        cur = _conn.execute(
            "DELETE FROM invites WHERE code = ? AND used_by IS NULL", (code,)
        )
        _conn.commit()
    return cur.rowcount > 0


def consume_invite(code: str, device_id: str, alias, cpu_model) -> str:
    """校验并消费注册码, 为设备签发/换发 Token, 返回新 Token。"""
    with _db_lock:
        row = _conn.execute("SELECT used_by, expires_at FROM invites WHERE code = ?", (code,)).fetchone()
        if row is None:
            raise ApiError(401, "invite not found")
        if row["used_by"] is not None:
            raise ApiError(410, "invite already used")
        if row["expires_at"] < utc_now_iso():
            raise ApiError(410, "invite expired")
        token = uuid.uuid4().hex
        now = utc_now_iso()
        _conn.execute("UPDATE invites SET used_by = ? WHERE code = ?", (device_id, code))
        # 设备行可能尚不存在(首次注册)或已存在(旧版 Token 心跳过)——两种都要落 Token。
        _conn.execute(
            """
            INSERT INTO devices (device_id, alias, cpu_model, first_seen, last_seen, api_token)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(device_id) DO UPDATE SET
                api_token = excluded.api_token,
                alias = COALESCE(excluded.alias, devices.alias),
                cpu_model = COALESCE(excluded.cpu_model, devices.cpu_model),
                last_seen = excluded.last_seen
            """,
            (device_id, alias, cpu_model, now, now, token),
        )
        _conn.commit()
    return token


def rotate_device_token(device_id: str) -> str:
    with _db_lock:
        row = _conn.execute("SELECT 1 FROM devices WHERE device_id = ?", (device_id,)).fetchone()
        if row is None:
            raise ApiError(404, "device not found")
        token = uuid.uuid4().hex
        _conn.execute("UPDATE devices SET api_token = ? WHERE device_id = ?", (token, device_id))
        _conn.commit()
    return token


def revoke_device_token(device_id: str) -> None:
    with _db_lock:
        row = _conn.execute("SELECT 1 FROM devices WHERE device_id = ?", (device_id,)).fetchone()
        if row is None:
            raise ApiError(404, "device not found")
        _conn.execute("UPDATE devices SET api_token = NULL WHERE device_id = ?", (device_id,))
        _conn.commit()


def resolve_device_token(token: str) -> str | None:
    with _db_lock:
        row = _conn.execute("SELECT device_id FROM devices WHERE api_token = ?", (token,)).fetchone()
    return row["device_id"] if row else None


def handle_enroll(payload: dict, invite: str) -> dict:
    device_id = payload.get("deviceId")
    if not isinstance(device_id, str) or not DEVICE_ID_RE.match(device_id):
        raise ApiError(400, "invalid deviceId")
    token = consume_invite(
        invite,
        device_id,
        _clamp_text(payload.get("alias"), TEXT_LIMITS["alias"]),
        _clamp_text(payload.get("cpuModel"), TEXT_LIMITS["cpuModel"]),
    )
    return {"ok": True, "deviceId": device_id, "token": token}


def list_devices() -> list[dict]:
    with _db_lock:
        rows = _conn.execute(
            "SELECT * FROM devices ORDER BY last_seen DESC"
        ).fetchall()
    result = [dict(r) for r in rows]
    for r in result:
        r.pop("api_token", None)  # 设备 Token 绝不出现在看板响应里
    return result


def device_detail(device_id: str) -> dict | None:
    with _db_lock:
        row = _conn.execute("SELECT * FROM devices WHERE device_id = ?", (device_id,)).fetchone()
        if row is None:
            return None
        history = _conn.execute(
            "SELECT id, ts, runtime_total, energy_total, power_last, power_peak, cores_grade"
            " FROM heartbeats WHERE device_id = ? ORDER BY id DESC LIMIT ?",
            (device_id, HISTORY_KEEP),
        ).fetchall()
    detail = dict(row)
    detail.pop("api_token", None)
    detail["history"] = [dict(h) for h in history]
    return detail


class Handler(BaseHTTPRequestHandler):
    server_version = "CoreBusyHub/1.35"
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):  # 轻量日志到 stdout, 容器可见
        print(f"[{utc_now_iso()}] {self.address_string()} {fmt % args}", flush=True)

    # -- helpers -------------------------------------------------------------
    def _is_admin(self) -> bool:
        return self.headers.get("X-Token", "") == TOKEN

    def _send_json(self, status: int, obj) -> None:
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _send_error_json(self, exc: ApiError) -> None:
        self._send_json(exc.status, {"error": exc.message})

    def _heartbeat_device_id(self, payload: dict) -> str:
        """心跳鉴权: 管理员 Token(旧版兼容)或设备 Token。返回允许上报的 device_id。"""
        supplied = self.headers.get("X-Token", "")
        if supplied == TOKEN:
            return str(payload.get("deviceId", ""))
        if not supplied:
            raise ApiError(401, "unauthorized")
        owner = resolve_device_token(supplied)
        if owner is None:
            raise ApiError(401, "unauthorized")
        if payload.get("deviceId") != owner:
            raise ApiError(403, "token does not match deviceId")
        return owner

    def _read_body(self, allow_empty: bool = False) -> bytes:
        try:
            length = int(self.headers.get("Content-Length") or 0)
        except ValueError:
            length = 0
        if length < 0 or length > MAX_BODY or (length == 0 and not allow_empty):
            raise ValueError("bad body length")
        return self.rfile.read(length)

    def _drain_body(self) -> None:
        try:
            self._read_body(allow_empty=True)
        except ValueError:
            self.close_connection = True

    def _json_body(self, raw: bytes) -> dict:
        payload = json.loads(raw.decode("utf-8") or "{}")
        if not isinstance(payload, dict):
            raise ValueError("body must be a json object")
        return payload

    # -- routes --------------------------------------------------------------
    def do_GET(self):
        path = urlparse(self.path).path
        if path == "/api/devices":
            if not self._is_admin():
                self._send_json(401, {"error": "unauthorized"})
                return
            self._send_json(200, {"devices": list_devices()})
            return
        if path.startswith("/api/devices/"):
            if not self._is_admin():
                self._send_json(401, {"error": "unauthorized"})
                return
            device_id = path[len("/api/devices/"):]
            if not DEVICE_ID_RE.match(device_id):
                self._send_json(400, {"error": "invalid device id"})
                return
            detail = device_detail(device_id)
            if detail is None:
                self._send_json(404, {"error": "not found"})
            else:
                self._send_json(200, detail)
            return
        if path == "/api/admin/invites":
            if not self._is_admin():
                self._send_json(401, {"error": "unauthorized"})
                return
            self._send_json(200, {"invites": list_invites()})
            return
        if path in ("/", "/index.html"):
            index_path = os.path.join(STATIC_DIR, "index.html")
            try:
                with open(index_path, "rb") as fh:
                    body = fh.read()
            except OSError:
                self._send_json(500, {"error": "dashboard missing"})
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        self._send_json(404, {"error": "not found"})

    def do_POST(self):
        path = urlparse(self.path).path
        try:
            # 先消费请求体再鉴权/校验: 否则 keep-alive 连接上的残留字节会被解析成下一请求
            raw = self._read_body(allow_empty=True)
            if path == "/api/heartbeat":
                payload = self._json_body(raw)
                self._heartbeat_device_id(payload)
                try:
                    result = handle_heartbeat(payload)
                except ValueError as exc:
                    self._send_json(400, {"error": str(exc)})
                    return
                self._send_json(200, result)
                return
            if path == "/api/enroll":
                invite = self.headers.get("X-Invite", "").strip()
                if not invite:
                    self._send_json(401, {"error": "missing X-Invite"})
                    return
                try:
                    payload = self._json_body(raw)
                    result = handle_enroll(payload, invite)
                except ValueError as exc:
                    self._send_json(400, {"error": str(exc)})
                    return
                self._send_json(200, result)
                return
            if path == "/api/admin/invites":
                if not self._is_admin():
                    self._send_json(401, {"error": "unauthorized"})
                    return
                self._send_json(200, create_invite())
                return
            if path.startswith("/api/admin/devices/"):
                if not self._is_admin():
                    self._send_json(401, {"error": "unauthorized"})
                    return
                rest = path[len("/api/admin/devices/"):]
                parts = rest.split("/")
                if len(parts) != 2 or parts[1] not in ("rotate", "revoke") or not DEVICE_ID_RE.match(parts[0]):
                    self._send_json(400, {"error": "invalid admin path"})
                    return
                if parts[1] == "rotate":
                    self._send_json(200, {"ok": True, "token": rotate_device_token(parts[0])})
                else:
                    revoke_device_token(parts[0])
                    self._send_json(200, {"ok": True})
                return
            self._send_json(404, {"error": "not found"})
        except ApiError as exc:
            self._send_error_json(exc)
        except (ValueError, json.JSONDecodeError) as exc:
            self._send_json(400, {"error": str(exc)})
        except sqlite3.Error as exc:
            self._send_json(500, {"error": f"db: {exc}"})

    def do_DELETE(self):
        path = urlparse(self.path).path
        self._drain_body()
        if path.startswith("/api/admin/invites/"):
            if not self._is_admin():
                self._send_json(401, {"error": "unauthorized"})
                return
            code = path[len("/api/admin/invites/"):]
            if delete_invite(code):
                self._send_json(200, {"ok": True})
            else:
                self._send_json(404, {"error": "invite not found or already used"})
            return
        self._send_json(404, {"error": "not found"})


def main() -> None:
    server = ThreadingHTTPServer(("0.0.0.0", PORT), Handler)
    source = "env" if TOKEN_ENV else "auto-generated (see data/token.txt)"
    print(f"CoreBusy hub listening on 0.0.0.0:{PORT}, data={DATA_DIR}, token={source}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
