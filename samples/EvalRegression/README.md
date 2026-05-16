# EvalRegression

End-to-end example of the Vais.Agents evaluation harness. Registers a `support-bot` agent, applies a 3-case `EvalSuite`, runs it, prints the results table, and exports JUnit XML — all from a single script.

## Assertions covered

| Case | Assertions |
|---|---|
| `refund-policy` | `no-turn-failed`, `tool-call-sequence` (search_kb called with refund query), `response-regex` |
| `billing-duplicate` | `no-turn-failed`, `judge-score` (LLM rates helpfulness ≥ 0.6), `metric-threshold` (duration ≤ 20 s) |
| `account-access` | `no-turn-failed`, `response-regex`, `metric-threshold` (total tokens ≤ 1000) |

## Prerequisites

- A running runtime with `OPENAI_API_KEY` exported on the runtime side.
- The `vais` CLI configured (`vais config use-context local` or the target context).
- An MCP server reachable at `http://localhost:3001` that exposes a `search_kb` tool (the `tool-call-sequence` assertion will fail if absent; the other assertions still run).

## Run

```bash
./run.ps1
```

Or step by step:

```bash
vais apply -f agent.yaml
vais apply -f eval-suite.yaml
vais eval run support-bot-regression --wait
vais eval results <run-id>
vais eval results <run-id> -o junit > eval-results.xml
```

## Diff against a baseline

After a successful run, copy the run id into `eval-suite.yaml`:

```yaml
spec:
  baseline:
    runId: eval-<your-run-id>
```

Re-apply and run again, then:

```bash
vais eval diff <baseline-run-id> <new-run-id>
```

## See also

- Tutorial: [`docs/agent-developer/evaluate-an-agent.md`](../../docs/agent-developer/evaluate-an-agent.md)
- Reference: `vais eval --help`
