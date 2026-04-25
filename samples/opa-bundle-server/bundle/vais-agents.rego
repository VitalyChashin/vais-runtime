package vais.agents

import rego.v1

# Default: deny-closed (explicit allow required).
default allow := {"allowed": false, "reason": "default deny"}

# Allow any invocation from a known principal.
allow := {"allowed": true} if {
    input.principal != null
    input.operation in {"Invoke", "Query", "Signal"}
}

# Allow agent create / update only from the ops tenant.
allow := {"allowed": true} if {
    input.principal != null
    input.principal.tenantId == "ops"
    input.operation in {"Create", "Update", "Evict", "Cancel"}
}
