# Budget cap for Vais.Agents.
#
# Strategy: deny Create / Update when the manifest's run budget
# exceeds operator-configured caps. Matches the shipped
# `RunBudget { MaxTurns, MaxToolCalls, MaxPromptTokens,
# MaxCompletionTokens, MaxDuration }` field set — each cap is
# independent.
#
# Override `max_*` defaults by supplying `data.vais.agents.config.*`
# via OPA bundle / ConfigMap.

package vais.agents

default max_prompt_tokens := 1000000
default max_completion_tokens := 500000
default max_turns := 50

default allow := {"allowed": true}

allow := {"allowed": false, "reason": sprintf("budget maxPromptTokens %d exceeds cap %d", [input.agent.budget.maxPromptTokens, max_prompt_tokens])} if {
    gated_operation
    input.agent != null
    input.agent.budget != null
    input.agent.budget.maxPromptTokens > max_prompt_tokens
}

allow := {"allowed": false, "reason": sprintf("budget maxCompletionTokens %d exceeds cap %d", [input.agent.budget.maxCompletionTokens, max_completion_tokens])} if {
    gated_operation
    input.agent != null
    input.agent.budget != null
    input.agent.budget.maxCompletionTokens > max_completion_tokens
}

allow := {"allowed": false, "reason": sprintf("budget maxTurns %d exceeds cap %d", [input.agent.budget.maxTurns, max_turns])} if {
    gated_operation
    input.agent != null
    input.agent.budget != null
    input.agent.budget.maxTurns > max_turns
}

gated_operation if { input.operation == "Create" }
gated_operation if { input.operation == "Update" }
