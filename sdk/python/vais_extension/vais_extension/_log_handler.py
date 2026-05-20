"""
Structured-log endpoint integration for vais-extension (Phase O-E).

When VAIS_LOG_ENDPOINT and VAIS_LOG_TOKEN are set (injected by the runtime or the
operator), this module installs a :class:`VaisLogHandler` on the root logger so that
every ``logging`` call in a container extension is forwarded to the runtime's
``POST /v1/logs`` endpoint.

The URL injected for extensions carries ``?source=extension&id=<extension-id>`` so
the runtime discriminates extension logs from plugin logs.

Usage: extension code uses standard Python ``logging``; no extra wiring needed::

    import logging
    log = logging.getLogger(__name__)
    log.info("Handler pre invoked for agent %s", context.agent_id)

Requires ``httpx``; add the ``[logs]`` extra when installing::

    pip install vais-extension[logs]
"""

from __future__ import annotations

import json
import logging
import os
from datetime import datetime, timezone
from typing import Any


class VaisLogHandler(logging.Handler):
    """
    Logging handler that POSTs structured log records to the runtime's
    ``POST /v1/logs`` endpoint.
    """

    _SEVERITY_MAP = {
        logging.CRITICAL: "CRITICAL",
        logging.ERROR:    "ERROR",
        logging.WARNING:  "WARN",
        logging.INFO:     "INFO",
        logging.DEBUG:    "DEBUG",
    }

    def __init__(self, endpoint: str, token: str, timeout: float = 2.0) -> None:
        super().__init__()
        self._endpoint = endpoint
        self._token = token
        self._timeout = timeout

    def emit(self, record: logging.LogRecord) -> None:
        try:
            import httpx

            severity = self._SEVERITY_MAP.get(record.levelno, "INFO")
            ts = datetime.fromtimestamp(record.created, tz=timezone.utc).strftime(
                "%Y-%m-%dT%H:%M:%S.") + f"{record.msecs:03.0f}Z"

            payload: dict[str, Any] = {
                "timestamp": ts,
                "severity":  severity,
                "message":   self.format(record),
            }

            extra: dict[str, Any] = {
                "logger":  record.name,
                "module":  record.module,
                "lineno":  record.lineno,
            }
            if record.exc_info:
                import traceback
                extra["exc"] = "".join(traceback.format_exception(*record.exc_info)).strip()

            payload["fields"] = extra

            httpx.post(
                self._endpoint,
                content=json.dumps(payload).encode(),
                headers={
                    "Content-Type": "application/json",
                    "Authorization": f"vais-plugin-token {self._token}",
                },
                timeout=self._timeout,
            )
        except Exception:
            self.handleError(record)


def _configure_log_handler() -> None:
    """
    Install :class:`VaisLogHandler` on the root logger when
    ``VAIS_LOG_ENDPOINT`` and ``VAIS_LOG_TOKEN`` are set.
    """
    endpoint = os.environ.get("VAIS_LOG_ENDPOINT", "").strip()
    token    = os.environ.get("VAIS_LOG_TOKEN",    "").strip()
    if not endpoint or not token:
        return

    try:
        import httpx  # noqa: F401 — verify the dep is available
    except ImportError:
        return

    handler = VaisLogHandler(endpoint, token)
    handler.setLevel(logging.DEBUG)
    logging.root.addHandler(handler)
