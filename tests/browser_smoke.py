"""可选：在真实 Chromium 中验证看板与临时 hub 的集成流程。仅标准库。"""
import argparse
from html.parser import HTMLParser
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import uuid


PROBE = r"""
<script>
(async () => {
  const checks = [], requests = [], errors = [];
  window.addEventListener('error', e => errors.push(e.message));
  window.addEventListener('unhandledrejection', e => errors.push(String(e.reason)));
  const originalFetch = window.fetch;
  window.fetch = async (...args) => {
    const response = await originalFetch(...args);
    requests.push({url:String(args[0]), status:response.status});
    return response;
  };
  const check = (ok, message) => { if (!ok) throw Error(message); checks.push(message); };
  const admin = '__ADMIN__';
  const deviceId = '0123456789abcdef';
  async function post(path, body, headers) {
    const response = await fetch(path, {method:'POST', headers, body:JSON.stringify(body)});
    check(response.ok, path + ' succeeds');
    return response.json();
  }
  try {
    localStorage.setItem('corebusy_hub_token', admin);
    await load();
    check($('rows').textContent.includes('暂无设备'), 'empty dashboard');
    const invitation = await post('/api/admin/invites', {}, {'X-Token':admin});
    const enrollment = await post('/api/enroll', {deviceId}, {'X-Invite':invitation.code});
    await load();
    openGrades(deviceId);
    check($('gbody').textContent.includes('暂无逐核等级数据'), 'unscored enrollment');
    closeGrades();
    await post('/api/heartbeat', {deviceId, alias:'浏览器回归设备', cpuModel:'Fixture CPU',
      coresPhysical:2, coresLogical:4, runtimeTotalSeconds:3600, energyTotalWh:12.5,
      powerLastW:40, powerPeakW:65,
      coresGrade:[{name:'"oncut=alert(1)',stage:0,level:2},{name:'C1',stage:2,level:30,score:98.5}]},
      {'X-Token':enrollment.token});
    await load();
    check($('rows').textContent.includes('浏览器回归设备'), 'heartbeat rendered');
    openGrades(deviceId);
    const badge = $('gbody').querySelector('.badge');
    let alerts = 0;
    window.alert = () => alerts++;
    badge.dispatchEvent(new Event('cut', {bubbles:true}));
    check(alerts === 0 && !badge.hasAttribute('oncut'), 'stored payload cannot execute or create event attribute');
    check(badge.title.startsWith('"oncut=alert(1)'), 'core name preserved as text');
    check($('gbody').textContent.includes('Lv30'), 'normal grades remain visible');
    closeGrades();
    localStorage.setItem('corebusy_hub_token', 'invalid-test-token');
    await load();
    check($('err').textContent.includes('Token 无效'), 'unauthorized error state');
    localStorage.setItem('corebusy_hub_token', admin);
    await load();
    check($('err').style.display === 'none', 'successful retry clears error');
    openGrades(deviceId);
    check(errors.length === 0, 'no unhandled browser errors');
    document.documentElement.dataset.testResult = 'passed';
  } catch (error) {
    errors.push(String(error));
    document.documentElement.dataset.testResult = 'failed';
  }
  const result = document.createElement('pre');
  result.id = 'browser-smoke-result'; result.hidden = true;
  result.textContent = JSON.stringify({checks, requests, errors});
  document.body.appendChild(result);
})();
</script>
"""


class ResultParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.status = None
        self.in_result = False
        self.result = ""

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "html":
            self.status = attrs.get("data-test-result")
        if tag == "pre" and attrs.get("id") == "browser-smoke-result":
            self.in_result = True

    def handle_endtag(self, tag):
        if tag == "pre":
            self.in_result = False

    def handle_data(self, data):
        if self.in_result:
            self.result += data


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--browser", required=True, help="Chromium/Chrome/Edge 可执行文件")
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    output.mkdir(parents=True, exist_ok=True)
    token = uuid.uuid4().hex  # 测试凭证，仅存在于临时服务。
    with tempfile.TemporaryDirectory(prefix="corebusy-browser-") as temporary:
        previous = {key: os.environ.get(key) for key in ("COREBUSY_HUB_DATA", "COREBUSY_HUB_TOKEN")}
        try:
            os.environ["COREBUSY_HUB_DATA"] = temporary
            os.environ["COREBUSY_HUB_TOKEN"] = token
            source = Path(__file__).resolve().parents[1] / "hub" / "main.py"
            spec = importlib.util.spec_from_file_location("browser_hub", source)
            hub = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(hub)
        finally:
            for key, value in previous.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value

        class Handler(hub.Handler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                if self.path == "/":
                    page = Path(hub.STATIC_DIR, "index.html").read_text(encoding="utf-8")
                    body = page.replace("</body>", PROBE.replace("__ADMIN__", token) + "</body>").encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "text/html; charset=utf-8")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                else:
                    super().do_GET()

        server = hub.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        worker = threading.Thread(target=server.serve_forever, daemon=True)
        worker.start()
        try:
            def native(path):
                path = str(path)
                if args.browser.lower().endswith(".exe") and path.startswith("/mnt/"):
                    return path[5].upper() + ":\\" + path[7:].replace("/", "\\")
                return path

            # profile 放在输出目录，WSL 调用 Windows Chromium 时也必须是 Windows 可访问路径。
            command = [args.browser, "--headless=new", "--no-first-run", "--no-default-browser-check",
                       "--disable-background-networking", "--disable-gpu", "--window-size=1280,900",
                       "--user-data-dir=" + native(output / "browser-profile"),
                       "--virtual-time-budget=10000", "--dump-dom",
                       "--screenshot=" + native(output / "dashboard-browser.png"),
                       "http://127.0.0.1:" + str(server.server_port) + "/"]
            result = subprocess.run(command, capture_output=True, timeout=45)
            dom = result.stdout.decode("utf-8", errors="replace")
            (output / "browser-dom.html").write_text(dom.replace(token, "[ephemeral-test-admin]"), encoding="utf-8")
            (output / "browser-stderr.txt").write_bytes(result.stderr)
            parsed = ResultParser()
            parsed.feed(dom)
            evidence = {"browserExit": result.returncode, "status": parsed.status,
                        "result": json.loads(parsed.result) if parsed.result else None}
            (output / "browser-result.json").write_text(json.dumps(evidence, ensure_ascii=False, indent=2))
            print(json.dumps(evidence, ensure_ascii=False, indent=2))
            return 0 if result.returncode == 0 and parsed.status == "passed" else 1
        finally:
            server.shutdown()
            server.server_close()
            worker.join()
            hub._conn.close()
            shutil.rmtree(output / "browser-profile")


if __name__ == "__main__":
    raise SystemExit(main())
