"""Reference container plugin for the sectioned-context endpoint (SC-24).

Demonstrates the opt-in flow:
1. Call ``build_sections()`` against the runtime gateway for the typed Section[] view.
2. Inspect / optionally mutate sections (this sample drops Metadata and prints a one-line
   breakdown for operator visibility).
3. Flatten via ``sections_to_openai_request()`` and call back through the runtime's
   gateway-proxied chat-completions endpoint.
"""
