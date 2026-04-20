# Integration-test fixture: allow only when the caller's tenant id
# matches the agent's `tenant` label. Deny with a structured reason on
# tenant mismatch so the adapter carries the reason through.
package vais.agents

default allow := {"allowed": false, "reason": "default-deny"}

allow := {"allowed": true} if {
    input.principal != null
    input.agent != null
    input.agent.labels.tenant == input.principal.tenantId
}

allow := {"allowed": false, "reason": "cross-tenant invocation denied"} if {
    input.principal != null
    input.agent != null
    input.agent.labels.tenant != input.principal.tenantId
}
