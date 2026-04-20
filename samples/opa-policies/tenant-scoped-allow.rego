# Tenant-scoped admission for Vais.Agents.
#
# Strategy: deny cross-tenant invocations. The adapter sends
#   input.principal.tenantId  — the caller's tenant id (JWT claim)
#   input.agent.labels.tenant — the agent's declared tenant label
# Operators match those for Invoke/Signal/Query; Create/Update stay
# allow-all so platform teams keep full control of manifest rollout.
#
# Install: mount this file into your OPA sidecar's policy dir.
# Configure the adapter: DataPath = "vais/agents/allow".

package vais.agents

# Default-deny for the gated operations; default-allow for others.
default allow := {"allowed": true}

# Invoke / Signal / Query must match tenant.
allow := {"allowed": true} if {
    gated_operation
    input.principal != null
    input.agent != null
    input.agent.labels.tenant == input.principal.tenantId
}

allow := {"allowed": false, "reason": "cross-tenant access denied"} if {
    gated_operation
    input.principal != null
    input.agent != null
    input.agent.labels.tenant != input.principal.tenantId
}

allow := {"allowed": false, "reason": "unauthenticated caller"} if {
    gated_operation
    input.principal == null
}

gated_operation if { input.operation == "Invoke" }
gated_operation if { input.operation == "Signal" }
gated_operation if { input.operation == "Query" }
