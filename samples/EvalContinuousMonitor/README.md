# EvalContinuousMonitor

Continuous production-eval example. Applies a `support-bot` agent and an `EvalSuite`
with `spec.sampling:` instead of `spec.cases:`. The runtime scores 10% of real
production runs every hour without replaying or altering any traffic.

## Assertions covered

| Assertion | What it checks |
|---|---|
| `no-turn-failed` | Agent turn completed without an unhandled exception |
| `response-regex` | Response is non-empty and contains at least 3 words |
| `judge-score` | LLM judge rates helpfulness ≥ 0.6 |

## Prerequisites

- A running runtime with `OPENAI_API_KEY` set on the runtime side.
- The `vais` CLI configured (`vais config use-context local` or the target context).
- *(Optional)* Prometheus + Grafana to view live metrics.

## Apply and send traffic

```bash
# Register the agent and the continuous eval suite.
vais apply -f agent.yaml
vais apply -f eval-suite.yaml

# Send a few production invocations to create traffic.
vais invoke support-bot --input "My account is locked, what should I do?"
vais invoke support-bot --input "I was charged twice last month."
vais invoke support-bot --input "How do I cancel my subscription?"

# Check which runs have been sampled and scored.
vais eval list --source continuous --suite support-bot-continuous
```

Example output:

```
EVAL RUN ID                                 SUITE                    STATUS   WINDOW START          SAMPLED
ceval-support-bot-continuous-20260518…      support-bot-continuous   Running  2026-05-18T14:00:00Z  1
```

## View results

```bash
# Get the eval-run detail for the latest window.
vais eval results ceval-support-bot-continuous-20260518…
```

## Grafana

Import `deploy/observability/grafana/dashboards/eval-continuous.json` into Grafana.
The dashboard shows live pass-rate, assertion score distributions, sampled-run count,
and window coverage. Live data appears within 30 s of the first sampled run.

| Panel | Metric | What to watch |
|---|---|---|
| Pass Rate | `vais_eval_continuous_cases_total` | Sustained drop below 95% |
| Assertion Score p90 | `vais_eval_continuous_assertion_score` | Falling `judge-score` p90 |
| Sampled Runs | `vais_eval_continuous_window_sampled` | Zero = no traffic |

## Cost management

At `rate: 0.10` × 100 rps × 1 judge call per sample = 10 judge calls/s. To reduce
cost, lower the rate or route judge calls through an `LlmGatewayConfig` with semantic
cache. Start at `rate: 0.001` while calibrating your assertion thresholds.

## Clean up

```bash
vais delete eval-suites/support-bot-continuous
vais delete support-bot
```

## See also

- Tutorial: [`docs/agent-developer/evaluate-an-agent.md`](../../docs/agent-developer/evaluate-an-agent.md) — Step 7
- Reference: `vais eval --help`
- `samples/EvalRegression/` — batch regression harness (complement to continuous monitoring)
