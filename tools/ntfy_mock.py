"""本地 ntfy 模拟服务器（v0.2.4 真机验收用）。

- GET  /<topic>/sse   订阅流（保持连接，实时转发该 topic 的新消息）
- POST /<topic>       接收一条消息（title= 头作为发件人），推送给订阅者并落盘
- GET  /_log          查看已收到的消息（下行推送验收用）

用法: python tools/ntfy_mock.py 8099
"""
import json
import queue
import sys
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8099

_lock = threading.Lock()
_subscribers: dict[str, list[queue.Queue]] = {}
_messages: list[dict] = []  # 全部收到的 POST（含下行推送）


def publish(topic: str, payload: dict):
    with _lock:
        subs = list(_subscribers.get(topic, []))
        _messages.append({"topic": topic, "time": time.strftime("%H:%M:%S"), **payload})
    for q in subs:
        try:
            q.put_nowait(payload)
        except queue.Full:
            pass


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        sys.stderr.write("[%s] %s\n" % (time.strftime("%H:%M:%S"), fmt % args))

    def do_GET(self):
        path = self.path.split("?")[0].rstrip("/")
        if path == "/_log":
            body = json.dumps(_messages, ensure_ascii=False, indent=1).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        if not path.endswith("/sse"):
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return

        topic = path[: -len("/sse")].strip("/")
        q: queue.Queue = queue.Queue(maxsize=100)
        with _lock:
            _subscribers.setdefault(topic, []).append(q)

        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "keep-alive")
        self.end_headers()
        try:
            self.wfile.write(b": connected\n\n")
            self.wfile.flush()
            while True:
                try:
                    payload = q.get(timeout=15)
                except queue.Empty:
                    self.wfile.write(b": keepalive\n\n")
                    self.wfile.flush()
                    continue
                data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
                self.wfile.write(b"id: %d\nevent: message\ndata: " % int(time.time() * 1000) + data + b"\n\n")
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass
        finally:
            with _lock:
                if topic in _subscribers and q in _subscribers[topic]:
                    _subscribers[topic].remove(q)

    def do_POST(self):
        topic = self.path.split("?")[0].strip("/")
        length = int(self.headers.get("Content-Length", 0))
        body = self.rfile.read(length).decode("utf-8", "replace") if length else ""
        title = self.headers.get("X-Title", topic)
        publish(topic, {
            "id": str(int(time.time() * 1000)),
            "time": int(time.time()),
            "topic": topic,
            "title": title,
            "message": body,
        })
        self.send_response(200)
        self.send_header("Content-Length", "0")
        self.end_headers()


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", PORT), Handler)
    print(f"ntfy mock on http://127.0.0.1:{PORT}  (topics: /work/sse /home/sse /phone/sse, log: /_log)")
    server.serve_forever()
