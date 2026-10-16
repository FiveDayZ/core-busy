"""真实 HTTP + 临时 SQLite；不连接任何用户后台。"""
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import threading
import unittest
from urllib.error import HTTPError
from urllib.request import Request, urlopen


class HubTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix="corebusy-hub-test-")
        previous = {k: os.environ.get(k) for k in ("COREBUSY_HUB_DATA", "COREBUSY_HUB_TOKEN")}
        try:
            os.environ["COREBUSY_HUB_DATA"] = cls.directory.name
            os.environ["COREBUSY_HUB_TOKEN"] = "isolated-test-admin"
            source = Path(__file__).resolve().parents[1] / "hub" / "main.py"
            spec = importlib.util.spec_from_file_location("test_corebusy_hub", source)
            cls.hub = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(cls.hub)
        finally:
            for key, value in previous.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        class QuietHandler(cls.hub.Handler):
            def log_message(self, *args):
                pass

        cls.server = cls.hub.ThreadingHTTPServer(("127.0.0.1", 0), QuietHandler)
        cls.thread = threading.Thread(target=cls.server.serve_forever, daemon=True)
        cls.thread.start()
        cls.base = "http://127.0.0.1:" + str(cls.server.server_port)

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.server.server_close()
        cls.thread.join()
        cls.hub._conn.close()
        cls.directory.cleanup()

    def request(self, method, path, payload=None, token=None, invite=None, raw=None):
        headers = {}
        if token:
            headers["X-Token"] = token
        if invite:
            headers["X-Invite"] = invite
        data = raw if raw is not None else (json.dumps(payload).encode() if payload is not None else None)
        if method == "POST" and data is None:
            data = b""
        request = Request(self.base + path, method=method, data=data, headers=headers)
        try:
            with urlopen(request, timeout=5) as response:
                return response.status, json.load(response)
        except HTTPError as error:
            with error:
                return error.code, json.load(error)

    def enroll(self, device):
        status, invite = self.request("POST", "/api/admin/invites", token=self.hub.TOKEN)
        self.assertEqual(status, 200)
        status, enrolled = self.request("POST", "/api/enroll", {"deviceId": device}, invite=invite["code"])
        self.assertEqual(status, 200)
        return enrolled["token"], invite["code"]

    def test_device_lifecycle_and_history(self):
        device = "1234567890abcdef"
        token, invite = self.enroll(device)
        self.assertEqual(self.request("POST", "/api/enroll", {"deviceId": device}, invite=invite)[0], 410)
        payload = {"deviceId": device, "runtimeTotalSeconds": 60, "energyTotalWh": 2.5,
                   "coresGrade": [{"name": '"oncut=alert(1)', "level": 2, "stage": 0}]}
        self.assertEqual(self.request("POST", "/api/heartbeat", payload, token)[0], 200)
        self.assertEqual(self.request("POST", "/api/heartbeat", {"deviceId": "aaaaaaaaaaaaaaaa"}, token)[0], 403)
        status, detail = self.request("GET", "/api/devices/" + device, token=self.hub.TOKEN)
        self.assertEqual(status, 200)
        self.assertEqual(detail["runtime_total"], 60)
        self.assertEqual(len(detail["history"]), 1)
        self.assertNotIn("api_token", detail)
        self.assertEqual(json.loads(detail["cores_grade"])[0]["name"], payload["coresGrade"][0]["name"])
        status, rotated = self.request("POST", "/api/admin/devices/" + device + "/rotate", token=self.hub.TOKEN)
        self.assertEqual(status, 200)
        self.assertEqual(self.request("POST", "/api/heartbeat", payload, token)[0], 401)
        self.assertEqual(self.request("POST", "/api/heartbeat", payload, rotated["token"])[0], 200)
        self.assertEqual(self.request("POST", "/api/admin/devices/" + device + "/revoke", token=self.hub.TOKEN)[0], 200)
        self.assertEqual(self.request("POST", "/api/heartbeat", payload, rotated["token"])[0], 401)

    def test_authentication_boundaries(self):
        self.assertEqual(self.request("GET", "/api/devices")[0], 401)
        self.assertEqual(self.request("POST", "/api/admin/invites")[0], 401)
        token, _ = self.enroll("fedcba0987654321")
        self.assertEqual(self.request("GET", "/api/devices", token=token)[0], 401)
        self.assertEqual(self.request("POST", "/api/admin/invites", token=token)[0], 401)

    def test_bad_bodies_do_not_create_records(self):
        for body in (b"[]", b"{broken", b'{"deviceId":"invalid"}'):
            self.assertEqual(self.request("POST", "/api/heartbeat", token=self.hub.TOKEN, raw=body)[0], 400)
        self.assertIsNone(self.hub.device_detail("invalid"))


if __name__ == "__main__":
    unittest.main()
