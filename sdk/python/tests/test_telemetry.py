"""Tests for _telemetry.py OTLP auto-configuration."""
from __future__ import annotations

import importlib
import os
import sys
from unittest.mock import MagicMock, patch

import pytest


def _reload_telemetry():
    """Re-import _telemetry with a clean module state."""
    mod_name = "vais_plugin._telemetry"
    if mod_name in sys.modules:
        del sys.modules[mod_name]
    return importlib.import_module(mod_name)


def test_configure_otlp_no_endpoint_noop(monkeypatch):
    """When OTEL_EXPORTER_OTLP_ENDPOINT is not set, _configure_otlp does nothing."""
    monkeypatch.delenv("OTEL_EXPORTER_OTLP_ENDPOINT", raising=False)
    mod = _reload_telemetry()
    # Should not raise; SDK import is not attempted.
    mod._configure_otlp()


def test_configure_otlp_sdk_not_installed_noop(monkeypatch):
    """When the OTel SDK is absent, _configure_otlp silently no-ops."""
    monkeypatch.setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:5001/v1/otlp")

    # Simulate the SDK not being installed by making the import fail.
    with patch.dict(sys.modules, {
        "opentelemetry": None,
        "opentelemetry.trace": None,
        "opentelemetry.sdk": None,
        "opentelemetry.sdk.trace": None,
        "opentelemetry.sdk.resources": None,
        "opentelemetry.sdk.trace.export": None,
        "opentelemetry.exporter.otlp.proto.http.trace_exporter": None,
    }):
        mod = _reload_telemetry()
        # Should not raise even when imports are unavailable.
        mod._configure_otlp()


def test_configure_otlp_sdk_installed_configures_provider(monkeypatch):
    """When endpoint is set and SDK is available, a TracerProvider is installed."""
    monkeypatch.setenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:5001/v1/otlp")

    fake_trace = MagicMock()
    fake_resource = MagicMock()
    fake_resource.create.return_value = MagicMock()
    fake_tracer_provider = MagicMock()
    fake_sdk_trace = MagicMock()
    fake_sdk_trace.TracerProvider.return_value = fake_tracer_provider
    fake_export = MagicMock()
    fake_exporter_mod = MagicMock()
    fake_exporter_mod.OTLPSpanExporter.return_value = MagicMock()

    with patch.dict(sys.modules, {
        "opentelemetry": MagicMock(trace=fake_trace),
        "opentelemetry.trace": fake_trace,
        "opentelemetry.sdk": MagicMock(),
        "opentelemetry.sdk.trace": fake_sdk_trace,
        "opentelemetry.sdk.resources": fake_resource,
        "opentelemetry.sdk.trace.export": fake_export,
        "opentelemetry.exporter": MagicMock(),
        "opentelemetry.exporter.otlp": MagicMock(),
        "opentelemetry.exporter.otlp.proto": MagicMock(),
        "opentelemetry.exporter.otlp.proto.http": MagicMock(),
        "opentelemetry.exporter.otlp.proto.http.trace_exporter": fake_exporter_mod,
    }):
        mod = _reload_telemetry()
        mod._configure_otlp()

    fake_trace.set_tracer_provider.assert_called_once()
