"""
Structured-log endpoint integration for vais-plugin.

When VAIS_LOG_ENDPOINT and VAIS_LOG_TOKEN are set (injected by the runtime's Docker
supervisor), this module installs a :class:`VaisLogHandler` on the root logger so that
every ``logging`` call in the plugin is forwarded to the runtime's ``POST /v1/logs``
endpoint.  The runtime fans the record out to its own ILogger pipeline (docker-logs,
ELK/Loki) and any configured structured-log sinks.

Wire shape posted per record::

    {
        "timestamp": "2026-05-20T12:34:56.789Z",
        "severity":  "INFO",
        "message":   "processed 42 records",
        "fields":    {"component": "loader", "count": 42}
    }

The handler is installed automatically when the env vars are present — plugin code
just uses standard Python ``logging`` and gets correlated routing for free::

    import logging
    log = logging.getLogger(__name__)
    log.info("Processed %d records", 42)

No extra dependencies required: ``httpx`` is a core vais-plugin dependency.
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

    Uses a synchronous ``httpx`` client with a short timeout so that a slow
    or unavailable runtime does not block plugin execution.  Failures are
    silenced via :meth:`handleError`.
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
        import httpx  # noqa: F401 — verify the dep is available before installing
    except ImportError:
        # httpx is a core dep; this branch is a safety net for unusual envs.
        return

    handler = VaisLogHandler(endpoint, token)
    handler.setLevel(logging.DEBUG)
    logging.root.addHandler(handler)
