# Integration-test fixture: deny Create when the agent's budget exceeds
# 100_000 maxTokens. Other verbs pass through.
package vais.agents

budget_cap := 100000

default allow := true

allow := {"allowed": false, "reason": "budget cap exceeded"} if {
    input.operation == "Create"
    input.agent != null
    input.agent.budget != null
    input.agent.budget.maxPromptTokens > budget_cap
}
