from http.server import HTTPServer, BaseHTTPRequestHandler
import json
import os

HANDLER_TYPE = "echo.EchoPlugin"
API_VERSION = "0.24"


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        if self.path == "/health":
            self._respond(200, {"status": "ok"})
        elif self.path == "/v1/metadata":
            self._respond(200, {"handlerTypeName": HANDLER_TYPE, "targetApiVersion": API_VERSION})
        else:
            self._respond(404, {"error": "not found"})

    def do_POST(self):
        if self.path == "/v1/invoke":
            length = int(self.headers.get("Content-Length", 0))
            body = json.loads(self.rfile.read(length) or b"{}")
            self._respond(200, {"result": body})
        else:
            self._respond(404, {"error": "not found"})

    def _respond(self, status, payload):
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", len(data))
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    port = int(os.getenv("PORT", 8080))
    print(f"echo-plugin listening on :{port}", flush=True)
    HTTPServer(("0.0.0.0", port), Handler).serve_forever()
