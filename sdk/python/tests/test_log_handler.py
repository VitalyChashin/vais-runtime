"""Tests for _log_handler.py structured-log endpoint integration."""
from __future__ import annotations

import importlib
import json
import logging
import sys
from unittest.mock import MagicMock, call, patch

import pytest


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _reload_plugin_log_handler():
    mod_name = "vais_plugin._log_handler"
    if mod_name in sys.modules:
        del sys.modules[mod_name]
    return importlib.import_module(mod_name)


def _reload_extension_log_handler():
    mod_name = "vais_extension._log_handler"
    if mod_name in sys.modules:
        del sys.modules[mod_name]
    return importlib.import_module(mod_name)


# ---------------------------------------------------------------------------
# Plugin SDK tests
# ---------------------------------------------------------------------------

class TestPluginConfigureLogHandler:
    def test_no_env_vars_noop(self, monkeypatch):
        monkeypatch.delenv("VAIS_LOG_ENDPOINT", raising=False)
        monkeypatch.delenv("VAIS_LOG_TOKEN", raising=False)
        initial_handlers = len(logging.root.handlers)
        mod = _reload_plugin_log_handler()
        mod._configure_log_handler()
        assert len(logging.root.handlers) == initial_handlers

    def test_missing_token_noop(self, monkeypatch):
        monkeypatch.setenv("VAIS_LOG_ENDPOINT", "http://localhost:5001/v1/logs?source=plugin&id=x")
        monkeypatch.delenv("VAIS_LOG_TOKEN", raising=False)
        initial_handlers = len(logging.root.handlers)
        mod = _reload_plugin_log_handler()
        mod._configure_log_handler()
        assert len(logging.root.handlers) == initial_handlers

    def test_both_vars_installs_handler(self, monkeypatch):
        monkeypatch.setenv("VAIS_LOG_ENDPOINT", "http://localhost:5001/v1/logs?source=plugin&id=x")
        monkeypatch.setenv("VAIS_LOG_TOKEN", "test-token")
        before = list(logging.root.handlers)
        mod = _reload_plugin_log_handler()
        mod._configure_log_handler()
        after = logging.root.handlers
        new_handlers = [h for h in after if h not in before]
        assert len(new_handlers) == 1
        assert isinstance(new_handlers[0], mod.VaisLogHandler)
        # Cleanup
        for h in new_handlers:
            logging.root.removeHandler(h)


class TestPluginVaisLogHandler:
    def test_emit_posts_to_endpoint(self, monkeypatch):
        mod = _reload_plugin_log_handler()
        handler = mod.VaisLogHandler("http://localhost:5001/v1/logs", "my-token", timeout=1.0)

        mock_post = MagicMock()
        with patch.dict(sys.modules, {"httpx": MagicMock(post=mock_post)}):
            record = logging.LogRecord(
                name="my.module", level=logging.INFO,
                pathname="", lineno=42, msg="hello world", args=(), exc_info=None,
            )
            handler.emit(record)

        mock_post.assert_called_once()
        _, kwargs = mock_post.call_args
        payload = json.loads(kwargs["content"])
        assert payload["severity"] == "INFO"
        assert payload["message"] == "hello world"
        assert "timestamp" in payload
        assert payload["fields"]["logger"] == "my.module"

    def test_emit_error_level(self, monkeypatch):
        mod = _reload_plugin_log_handler()
        handler = mod.VaisLogHandler("http://x", "t")

        mock_post = MagicMock()
        with patch.dict(sys.modules, {"httpx": MagicMock(post=mock_post)}):
            record = logging.LogRecord(
                name="x", level=logging.ERROR,
                pathname="", lineno=1, msg="boom", args=(), exc_info=None,
            )
            handler.emit(record)

        payload = json.loads(mock_post.call_args[1]["content"])
        assert payload["severity"] == "ERROR"

    def test_emit_httpx_unavailable_does_not_raise(self):
        mod = _reload_plugin_log_handler()
        handler = mod.VaisLogHandler("http://x", "t")

        with patch.dict(sys.modules, {"httpx": None}):
            record = logging.LogRecord(
                name="x", level=logging.WARNING,
                pathname="", lineno=1, msg="warn", args=(), exc_info=None,
            )
            # Should not propagate — handleError swallows it
            handler.emit(record)

    def test_emit_sends_auth_header(self):
        mod = _reload_plugin_log_handler()
        handler = mod.VaisLogHandler("http://x", "secret-tok")

        mock_post = MagicMock()
        with patch.dict(sys.modules, {"httpx": MagicMock(post=mock_post)}):
            record = logging.LogRecord(
                name="x", level=logging.DEBUG,
                pathname="", lineno=1, msg="d", args=(), exc_info=None,
            )
            handler.emit(record)

        headers = mock_post.call_args[1]["headers"]
        assert headers["Authorization"] == "vais-plugin-token secret-tok"
        assert headers["Content-Type"] == "application/json"


# ---------------------------------------------------------------------------
# Extension SDK tests
# ---------------------------------------------------------------------------

class TestExtensionConfigureLogHandler:
    def test_no_env_vars_noop(self, monkeypatch):
        monkeypatch.delenv("VAIS_LOG_ENDPOINT", raising=False)
        monkeypatch.delenv("VAIS_LOG_TOKEN", raising=False)
        initial_handlers = len(logging.root.handlers)
        mod = _reload_extension_log_handler()
        mod._configure_log_handler()
        assert len(logging.root.handlers) == initial_handlers

    def test_both_vars_installs_handler(self, monkeypatch):
        monkeypatch.setenv("VAIS_LOG_ENDPOINT", "http://runtime:5001/v1/logs?source=extension&id=ext")
        monkeypatch.setenv("VAIS_LOG_TOKEN", "ext-token")
        before = list(logging.root.handlers)
        mod = _reload_extension_log_handler()
        mod._configure_log_handler()
        after = logging.root.handlers
        new_handlers = [h for h in after if h not in before]
        assert len(new_handlers) == 1
        assert isinstance(new_handlers[0], mod.VaisLogHandler)
        for h in new_handlers:
            logging.root.removeHandler(h)
