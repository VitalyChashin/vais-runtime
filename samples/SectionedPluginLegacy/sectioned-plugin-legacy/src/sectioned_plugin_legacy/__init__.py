"""Plugin-side-flatten variant of the SectionedPlugin sample.

Side-by-side companion to ``samples/SectionedPlugin/`` — same goal (opt-in to the typed
``Section[]`` view), different path:

  SectionedPlugin (canonical, v0.27):
      build_sections()  -> inspect/mutate -> complete_from_sections()
                                              ^ runtime flattens + telemetry symmetry

  SectionedPluginLegacy (this sample, pre-v0.27 shape):
      build_sections()  -> inspect/mutate -> sections_to_openai_request()
                                          -> POST /chat/completions
                                              ^ plugin flattens; telemetry doesn't fire on
                                                the LLM-call span

The legacy path is preserved indefinitely as a backwards-compatibility and escape-hatch
contract — necessary when the plugin needs the OpenAI chat-completions wire shape on the
client side (e.g. integrating with a non-VAIS toolchain). For new plugins that don't have
that constraint, prefer the canonical path.
"""
