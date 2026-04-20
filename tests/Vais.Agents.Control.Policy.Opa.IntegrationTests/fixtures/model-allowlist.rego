# Integration-test fixture: Create / Update verbs must use a model
# provider from the allowlist. Other operations pass through.
package vais.agents

allowed_providers := {"openai", "anthropic"}

default allow := true

allow := {"allowed": false, "reason": "model provider not in allowlist"} if {
    input.operation == "Create"
    input.agent != null
    input.agent.model != null
    not allowed_providers[input.agent.model.provider]
}
