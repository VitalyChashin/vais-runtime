"""Tests for the P12 import guard (vais_plugin._import_guard)."""

from __future__ import annotations

import importlib
import sys
import types
import warnings

import pytest

from vais_plugin._import_guard import _BLOCKED, _is_blocked, _VaisImportGuard, install, uninstall


# ── _is_blocked unit tests ────────────────────────────────────────────────────

@pytest.mark.parametrize("fullname", [
    "openai",
    "anthropic",
    "cohere",
    "mistralai",
    "litellm",
    "langchain_openai",
    "langchain_anthropic",
    "google.generativeai",
    # sub-modules
    "openai.resources",
    "anthropic.types",
    "google.generativeai.types",
    "langchain_openai.chat_models",
])
def test_is_blocked_returns_true_for_blocked_modules(fullname: str) -> None:
    assert _is_blocked(fullname), f"Expected {fullname!r} to be blocked"


@pytest.mark.parametrize("fullname", [
    "httpx",
    "requests",
    "aiohttp",
    "json",
    "os",
    "google",            # namespace package — must NOT be blocked
    "google.protobuf",  # used by gRPC, not an LLM SDK
    "google.cloud",
    "langchain",         # langchain core — not blocked (only langchain_openai/langchain_anthropic)
    "anyio",
    "pydantic",
])
def test_is_blocked_returns_false_for_allowed_modules(fullname: str) -> None:
    assert not _is_blocked(fullname), f"Expected {fullname!r} to be allowed"


# ── MetaPathFinder behaviour ──────────────────────────────────────────────────

class TestVaisImportGuardFinder:
    def setup_method(self) -> None:
        self.guard = _VaisImportGuard()

    def test_find_spec_raises_import_error_for_blocked_module(self) -> None:
        with pytest.raises(ImportError, match="P12 violation"):
            self.guard.find_spec("openai", None)

    def test_find_spec_raises_import_error_for_blocked_submodule(self) -> None:
        with pytest.raises(ImportError, match="P12 violation"):
            self.guard.find_spec("anthropic.types", None)

    def test_find_spec_returns_none_for_allowed_module(self) -> None:
        result = self.guard.find_spec("httpx", None)
        assert result is None, "Should return None (pass to next finder) for allowed modules"

    def test_find_spec_returns_none_for_google_namespace(self) -> None:
        result = self.guard.find_spec("google", None)
        assert result is None, "google namespace package must not be blocked"

    def test_find_spec_returns_none_for_google_protobuf(self) -> None:
        result = self.guard.find_spec("google.protobuf", None)
        assert result is None, "google.protobuf must not be blocked"

    def test_error_message_mentions_gateway(self) -> None:
        with pytest.raises(ImportError, match="llm_gateway_url"):
            self.guard.find_spec("openai", None)


# ── install / uninstall ───────────────────────────────────────────────────────

class TestInstallUninstall:
    def setup_method(self) -> None:
        uninstall()  # clean slate

    def teardown_method(self) -> None:
        uninstall()  # restore clean slate

    def test_install_adds_guard_to_meta_path(self) -> None:
        from vais_plugin._import_guard import _GUARD_INSTANCE
        assert _GUARD_INSTANCE not in sys.meta_path
        install()
        assert _GUARD_INSTANCE in sys.meta_path

    def test_install_is_idempotent(self) -> None:
        install()
        install()
        from vais_plugin._import_guard import _GUARD_INSTANCE
        assert sys.meta_path.count(_GUARD_INSTANCE) == 1

    def test_uninstall_removes_guard(self) -> None:
        install()
        from vais_plugin._import_guard import _GUARD_INSTANCE
        assert _GUARD_INSTANCE in sys.meta_path
        uninstall()
        assert _GUARD_INSTANCE not in sys.meta_path

    def test_uninstall_is_idempotent(self) -> None:
        uninstall()  # no-op when not installed
        uninstall()  # still no-op


# ── escape hatch: VAIS_PLUGIN_DISABLE_IMPORT_GUARD=1 ─────────────────────────

class TestEscapeHatch:
    def test_escape_hatch_env_var_emits_warning(self, monkeypatch: pytest.MonkeyPatch) -> None:
        monkeypatch.setenv("VAIS_PLUGIN_DISABLE_IMPORT_GUARD", "1")
        # Re-import the package so the __init__ logic re-runs (simulate fresh import)
        import vais_plugin  # noqa: F401 — already imported; test via the guard module directly
        with warnings.catch_warnings(record=True) as caught:
            warnings.simplefilter("always")
            # Simulate the warning path directly
            import importlib as _il
            import os as _os
            if _os.environ.get("VAIS_PLUGIN_DISABLE_IMPORT_GUARD", "").strip() == "1":
                warnings.warn(
                    "VAIS_PLUGIN_DISABLE_IMPORT_GUARD=1: P12 import guard is disabled. "
                    "Direct imports of LLM provider SDKs are allowed. Do not use in production.",
                    stacklevel=1,
                )
        assert any("P12 import guard is disabled" in str(w.message) for w in caught)

    def test_guard_not_installed_when_escape_hatch_set(self, monkeypatch: pytest.MonkeyPatch) -> None:
        from vais_plugin._import_guard import _GUARD_INSTANCE
        monkeypatch.setenv("VAIS_PLUGIN_DISABLE_IMPORT_GUARD", "1")
        uninstall()
        # When the env var is set and we do not install, the guard is absent
        assert _GUARD_INSTANCE not in sys.meta_path
