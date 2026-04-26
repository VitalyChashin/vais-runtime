"""OpenTelemetry setup and W3C traceparent context helpers (v0.24)."""
from __future__ import annotations

import os
from typing import Optional

try:
    from opentelemetry import context as otel_context
    from opentelemetry import trace
    from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
    from opentelemetry.sdk.trace import TracerProvider
    from opentelemetry.sdk.trace.export import BatchSpanProcessor
    from opentelemetry.trace.propagation.tracecontext import TraceContextTextMapPropagator

    _OTEL_AVAILABLE = True
except ImportError:  # pragma: no cover
    _OTEL_AVAILABLE = False

_propagator = TraceContextTextMapPropagator() if _OTEL_AVAILABLE else None
_provider_initialised = False


def setup_otel() -> None:
    """Configure a TracerProvider that exports to OTEL_EXPORTER_OTLP_ENDPOINT.

    Idempotent — subsequent calls are no-ops. If the endpoint env var is absent
    or OTel packages are not installed, this is a no-op.
    """
    global _provider_initialised
    if not _OTEL_AVAILABLE or _provider_initialised:
        return
    _provider_initialised = True

    endpoint = os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT", "")
    if not endpoint:
        return

    provider = TracerProvider()
    provider.add_span_processor(
        BatchSpanProcessor(OTLPSpanExporter(endpoint=endpoint))
    )
    trace.set_tracer_provider(provider)


def extract_context(traceparent: Optional[str]) -> object:
    """Return an OTel context with *traceparent* as the remote parent, or the
    current ambient context when *traceparent* is None."""
    if not _OTEL_AVAILABLE or not traceparent:
        return otel_context.get_current() if _OTEL_AVAILABLE else object()
    return _propagator.extract({"traceparent": traceparent})


def attach_context(ctx: object) -> object:
    """Attach *ctx* as the ambient OTel context and return the token."""
    if not _OTEL_AVAILABLE:
        return None
    return otel_context.attach(ctx)  # type: ignore[arg-type]


def detach_context(token: object) -> None:
    """Detach a previously attached context token."""
    if not _OTEL_AVAILABLE or token is None:
        return
    otel_context.detach(token)  # type: ignore[arg-type]
