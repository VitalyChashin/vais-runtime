"""
Optional OTLP telemetry auto-configuration for vais-plugin.

When OTEL_EXPORTER_OTLP_ENDPOINT is set (injected by the runtime's Docker supervisor),
this module configures the OpenTelemetry SDK to export spans to the runtime's OTLP
receiver over HTTP/protobuf.  The receiver re-emits the spans under the correct trace
context so they appear alongside the surrounding graph-node span in Langfuse and other
backends.

Activation requires the ``otlp`` optional dependency group::

    pip install vais-plugin[otlp]

If the OpenTelemetry SDK or the OTLP exporter is not installed, the module silently
no-ops — plugins work normally but without forwarding spans.
"""

from __future__ import annotations

import os


def _configure_otlp() -> None:
    """Configure OTLP export if OTEL_EXPORTER_OTLP_ENDPOINT is set and the SDK is available."""
    endpoint = os.environ.get("OTEL_EXPORTER_OTLP_ENDPOINT", "").strip()
    if not endpoint:
        return

    try:
        from opentelemetry import trace
        from opentelemetry.sdk.resources import Resource
        from opentelemetry.sdk.trace import TracerProvider
        from opentelemetry.sdk.trace.export import BatchSpanProcessor
        from opentelemetry.exporter.otlp.proto.http.trace_exporter import OTLPSpanExporter
    except ImportError:
        # otlp extra not installed — silently no-op.
        return

    # Resource attributes are merged from OTEL_RESOURCE_ATTRIBUTES env var automatically by
    # the SDK.  The runtime injects vais.agent_id there; no manual merge needed.
    resource = Resource.create()

    provider = TracerProvider(resource=resource)

    # OTLPSpanExporter reads OTEL_EXPORTER_OTLP_ENDPOINT, OTEL_EXPORTER_OTLP_PROTOCOL,
    # and OTEL_EXPORTER_OTLP_HEADERS from the environment, so no explicit args needed.
    exporter = OTLPSpanExporter()
    provider.add_span_processor(BatchSpanProcessor(exporter))

    trace.set_tracer_provider(provider)
