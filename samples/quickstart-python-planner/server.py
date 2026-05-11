"""Minimal container plugin that speaks the IP-1 HTTP protocol.

Endpoints:
  GET  /health        — readiness probe
  GET  /v1/metadata   — handler identity (validated by the runtime at startup)
  POST /v1/invoke     — receives the enriched messages list, returns assistantMessage

The plugin calls OpenAI directly. The runtime's `llmGatewayUrl` is not used in this
sample — see research/plugin-container-model-2026-05-08.md for the future gateway-injection
path that routes plugin LLM calls through the runtime middleware chain.
"""

import json
import os
from http.server import BaseHTTPRequestHandler, HTTPServer

from openai import OpenAI

HANDLER_TYPE = "quickstart_planner.QuickstartPlanner"
API_VERSION = "0.24"

SYSTEM_PROMPT = (
    "You decompose a user research query into exactly three sub-questions "
    "that together cover the topic. Reply with three lines, no preamble, "
    "one sub-question per line."
)

_client = OpenAI(api_key=os.environ["OPENAI_API_KEY"])


def plan(messages: list[dict]) -> str:
    user_text = next((m.get("content", "") for m in reversed(messages) if m.get("role") == "user"), "")
    completion = _client.chat.completions.create(
        model="gpt-4o-mini",
        messages=[
            {"role": "system", "content": SYSTEM_PROMPT},
            {"role": "user", "content": user_text},
        ],
    )
    return completion.choices[0].message.content or ""


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def do_GET(self):
        if self.path == "/health":
            self._respond(200, {"status": "ok"})
        elif self.path == "/v1/metadata":
            self._respond(200, {"handlerTypeName": HANDLER_TYPE, "targetApiVersion": API_VERSION})
        else:
            self._respond(404, {"errorType": "NotFound", "errorMessage": self.path})

    def do_POST(self):
        if self.path != "/v1/invoke":
            self._respond(404, {"errorType": "NotFound", "errorMessage": self.path})
            return

        length = int(self.headers.get("Content-Length", 0))
        body = json.loads(self.rfile.read(length) or b"{}")

        try:
            assistant_message = plan(body.get("messages", []))
        except Exception as ex:
            self._respond(500, {
                "errorType": "InternalError",
                "errorMessage": str(ex),
                "diagnosticTail": "",
            })
            return

        self._respond(200, {
            "assistantMessage": assistant_message,
            "opaqueState": None,
        })

    def _respond(self, status, payload):
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


if __name__ == "__main__":
    port = int(os.getenv("PORT", "8080"))
    print(f"quickstart-python-planner listening on :{port}", flush=True)
    HTTPServer(("0.0.0.0", port), Handler).serve_forever()
