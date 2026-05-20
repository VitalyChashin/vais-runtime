"""
Telemetry helpers for Vais.Agents container extensions.

Requires the ``[telemetry]`` optional extra::

    pip install vais-extension[telemetry]

Usage::

    from vais_extension.telemetry import extract_parent_context, span

    @app.post("/handlers/mem-load/pre")
    async def pre(req: Request, body: AgentInputPreRequest):
        parent = extract_parent_context(req.headers)
        with span("mem0.load", parent_ctx=parent, collection="user-memories") as s:
            results = await mem0.search(...)
            s.set_attribute("result_count", len(results))
            return PreResponse(action="next")

When ``opentelemetry-api`` is not installed every function in this module is
a no-op — the extension runs normally but emits no spans.
"""

from __future__ import annotations

from contextlib import contextmanager
from typing import Any, Iterator, Mapping, Optional

try:
    from opentelemetry import propagate as _propagate
    from opentelemetry import trace as _trace

    _tracer = _trace.get_tracer("vais.extension")
    _OTEL_AVAILABLE = True
except ImportError:
    _OTEL_AVAILABLE = False


def extract_parent_context(headers: Mapping[str, str]) -> Optional[Any]:
    """
    Extract the W3C trace context the runtime injected into the HTTP request headers.

    Pass the returned value as *parent_ctx* to :func:`span` so the extension's
    spans appear as children of the runtime's ``vais.extension.handler.invoke``
    span in Langfuse / Grafana Tempo.

    Returns an OTel ``Context`` object, or ``None`` when ``opentelemetry-api``
    is not installed.
    """
    if not _OTEL_AVAILABLE:
        return None
    return _propagate.extract(headers)


@contextmanager
def span(
    name: str,
    *,
    parent_ctx: Optional[Any] = None,
    **attributes: Any,
) -> Iterator[Any]:
    """
    Emit a child span correctly parented under the runtime's invocation span.

    When ``opentelemetry-api`` is not installed this is a no-op context manager —
    the body runs normally but no span is emitted.

    :param name: Span operation name. Convention: ``<extension-id>.<operation>``,
                 e.g. ``mem0.search`` or ``vais-ext-log.pre``.
    :param parent_ctx: Context returned by :func:`extract_parent_context`. When
                       omitted the span is parented to whatever is current in the
                       process — usually nothing outside of a test harness.
    :param attributes: Initial span attributes set before the body runs.

    Example::

        parent = extract_parent_context(request.headers)
        with span("mem0.query", parent_ctx=parent, collection="user") as s:
            results = await mem0.search(query)
            s.set_attribute("result_count", len(results))
    """
    if not _OTEL_AVAILABLE:

        class _NoOp:
            def set_attribute(self, *a: Any, **kw: Any) -> None: ...
            def set_status(self, *a: Any, **kw: Any) -> None: ...
            def record_exception(self, *a: Any, **kw: Any) -> None: ...

        yield _NoOp()
        return

    with _tracer.start_as_current_span(name, context=parent_ctx) as s:
        for key, value in attributes.items():
            s.set_attribute(key, value)
        yield s
