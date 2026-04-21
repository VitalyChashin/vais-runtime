# Example allow-all Rego policy for the docker-compose OPA overlay.
#
# This is a trivially permissive starter policy that keeps the full control-plane
# verb surface open. Use it to verify the OPA round-trip works end to end, then
# swap in one of the richer samples under `../../../samples/opa-policies/`
# (tenant-scoped-allow.rego / model-provider-allowlist.rego / budget-cap.rego)
# for real gating.
#
# Wired via `DataPath = "vais/agents/allow"` — the runtime default when no
# VAIS_OPA_DATAPATH env var is set.

package vais.agents

default allow := {"allowed": true}
