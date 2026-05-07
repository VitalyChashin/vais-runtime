# Max-concurrent-runs cap for Vais.Agents.
#
# Strategy: deny Invoke when the tenant's active-run count meets or
# exceeds a configured cap. Requires OPA to receive runtime state via
# a push-data endpoint or bundle — the agent host posts
#   PUT /v1/data/vais/agents/state  {"active_runs_by_tenant": {"tenant-42": 8}}
# before every evaluation (or use the OPA status plugin for bundle pulls).
#
# The cap is configurable per-tenant via data.vais.agents.config.max_concurrent_runs
# (number) or per-tenant via data.vais.agents.config.tenant_caps (object).
#
# Defaults: 10 concurrent runs per tenant.

package vais.agents

default max_concurrent_runs := 10

default allow := {"allowed": true}

allow := {"allowed": false, "reason": "concurrent run limit reached for this tenant"} if {
    input.operation == "Invoke"
    tenant := input.tenant
    tenant != null
    active := data.vais.agents.state.active_runs_by_tenant[tenant]
    cap    := effective_cap(tenant)
    active >= cap
}

# Per-tenant override takes priority; fall back to global cap.
effective_cap(tenant) := c if {
    c := data.vais.agents.config.tenant_caps[tenant]
} else := c if {
    c := data.vais.agents.config.max_concurrent_runs
} else := max_concurrent_runs
