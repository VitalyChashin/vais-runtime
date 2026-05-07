# Model-provider allowlist for Vais.Agents.
#
# Strategy: on Create / Update, deny any manifest whose model.provider
# isn't in the configured set. Common fit for platform teams who
# centrally manage approved LLM vendors (billing, data-residency,
# compliance).
#
# The allowlist lives in `data.vais.agents.config.allowed_providers`
# — either baked into this file, loaded as a separate data.json, or
# fetched from an OPA bundle. Uncomment the line below to opt into a
# hard-coded set; otherwise supply via `data` mount.

package vais.agents

# Hard-coded default. Override by supplying `data.vais.agents.allowed_providers`.
default allowed_providers := {"openai", "anthropic", "azureOpenAi"}

default allow := {"allowed": true}

allow := {"allowed": false, "reason": "model provider not in allowlist"} if {
    gated_operation
    input.agent != null
    input.agent.model != null
    not allowed_providers[input.agent.model.provider]
}

gated_operation if { input.operation == "Create" }
gated_operation if { input.operation == "Update" }
