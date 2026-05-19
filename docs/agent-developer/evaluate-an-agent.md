# Evaluate an agent

You'll author a 3-case regression suite that scores a customer support agent on response shape and judge-rated quality; publish the results as JUnit XML for CI; and diff two runs side-by-side to see exactly what changed. About 25 minutes. End state: `vais eval run support-bot-regression --wait` gates your CI pipeline, and `vais eval diff` surfaces regressions before they reach production.

## Why this matters

Shipping an agent is step one. Knowing whether the next model version, a prompt tweak, or a tool API change *broke* it is step two. The Vais.Agents evaluation harness gives you:

- **Declarative suites** — `kind: EvalSuite` lives alongside your agent manifests; `vais apply` publishes it like every other resource (P11).
- **Composable assertions** — ten built-in scoring kinds; one `custom` seam for your own scorers. No shell hooks, no out-of-process judge services.
- **Reproducible diffs** — `vais eval diff <baseline> <candidate>` shows per-case and per-assertion deltas between two runs.
- **JUnit XML** — drop-in gate for any CI system that understands standard test reports.

## Prerequisites

- A running runtime ([DevOps section](../devops/index.md)).
- The CLI pointed at it (`vais config use-context local`).
- `OPENAI_API_KEY` exported on the runtime side (used by the agent and by the judge assertion in Step 1).
- Completed [Your first declarative agent](your-first-declarative-agent.md) — you're comfortable with `vais apply` and `vais invoke`.

## Step 1 — Register the target agent

This tutorial evaluates a `support-bot` agent that handles customer support queries. If you already have a registered agent you'd like to evaluate, skip to Step 2 and swap in its id wherever you see `support-bot`.

Save as `support-bot.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: support-bot
  version: "1.0"
  description: Customer support agent.
spec:
  model:
    provider: openai
    id: gpt-4o-mini
    apiKeyRef: secret://env/OPENAI_API_KEY
  systemPrompt:
    inline: |
      You are a helpful customer support assistant.
      Acknowledge the customer's issue first, then propose a concrete next step.
      Keep replies under 80 words.
  handler:
    typeName: declarative
  protocols:
    - kind: Http
```

```bash
vais apply -f support-bot.yaml
```

## Step 2 — Author the suite

An `EvalSuite` is a manifest with a target, defaults, and a list of cases. Each case carries an `assertions` list; the runner evaluates every assertion and passes the case only if all of them pass.

Save as `eval-support-bot.yaml`:

```yaml
apiVersion: vais.agents/v1
kind: EvalSuite
metadata:
  id: support-bot-regression
  version: "1.0"
  description: Regression suite for the customer support agent.
spec:
  agentId: support-bot
  defaults:
    judgeModel: gpt-4o-mini
    timeout: "00:00:30"
  cases:

    - id: refund-policy
      name: Refund policy query
      input: "How do I request a refund for an overcharge?"
      assertions:
        - kind: no-turn-failed
        - kind: response-regex
          params:
            pattern: "refund|policy|30.?day"
            flags: ignoreCase
        - kind: metric-threshold
          params:
            metric: duration
            max: 20000

    - id: billing-duplicate
      name: Duplicate charge complaint
      input: "I was charged twice for plan Pro last Tuesday."
      assertions:
        - kind: no-turn-failed
        - kind: judge-score
          params:
            prompt: |
              The customer reported a duplicate charge on plan Pro.
              The agent should acknowledge the issue and offer a concrete next step
              (e.g., ask for transaction details, initiate an investigation, or promise a refund).
              Score 0..1: 1 = helpful and actionable, 0 = irrelevant or dismissive.
            minScore: 0.6
        - kind: metric-threshold
          params:
            metric: duration
            max: 20000

    - id: account-access
      name: Cannot log in
      input: "I can't log in to my account. I've tried resetting my password twice."
      assertions:
        - kind: no-turn-failed
        - kind: response-regex
          params:
            pattern: "password|reset|account|access|login"
            flags: ignoreCase
        - kind: metric-threshold
          params:
            metric: totalTokens
            max: 1000
```

**Assertions used:**

| Kind | What it checks |
|---|---|
| `no-turn-failed` | No `TurnFailed` event in the run — the agent didn't crash or throw. |
| `response-regex` | The agent's text reply matches the pattern (case-insensitive). |
| `judge-score` | An LLM judge scores the reply 0–1; pass threshold is `minScore`. |
| `metric-threshold` | `duration` (wall-clock ms) and `totalTokens` (prompt + completion) must stay under `max`. |

Apply the suite:

```bash
vais apply -f eval-support-bot.yaml
vais get-eval-suites support-bot-regression
```

The command returns the full suite manifest YAML, confirming the id, version, and case list.

## Step 3 — Run the suite

`vais eval run` starts the suite and returns an eval run id immediately. Pass `--wait` to subscribe to the SSE progress stream and watch cases score in real time:

```bash
vais eval run support-bot-regression --wait
```

```
started evalRunId=5641e831...
  pass refund-policy
  pass billing-duplicate
  pass account-access
run completed
```

The eval run id is printed on the first line. If you launched without `--wait`, retrieve it from the list:

```bash
vais eval list --suite support-bot-regression
```

```
RUN ID              SUITE                    STATUS     PASS  FAIL  STARTED
5641e831...         support-bot-regression   completed  3     0     2026-05-16 13:21
```

## Step 4 — Read results

`vais eval results` renders a case × assertion matrix for a run:

```bash
vais eval results 5641e831...
```

```
support-bot-regression v1.0 — run 5641e831...
Status: Completed  Pass: 3  Fail: 0  Total: 3

┌───────────────────┬────────┬────────────────────────────────────────────────────┐
│ CASE              │ STATUS │ ASSERTIONS                                         │
├───────────────────┼────────┼────────────────────────────────────────────────────┤
│ refund-policy     │ pass   │ no-turn-failed✓, response-regex✓, metric-threshold✓│
│ billing-duplicate │ pass   │ no-turn-failed✓, judge-score✓, metric-threshold✓   │
│ account-access    │ pass   │ no-turn-failed✓, response-regex✓, metric-threshold✓│
└───────────────────┴────────┴────────────────────────────────────────────────────┘
```

The ASSERTIONS column lists every assertion by kind with a pass (✓), fail (✗), or error (!) marker. For scripting or audit, `-o json` returns the full result with per-assertion scores and diagnostics:

```bash
vais eval results 5641e831... -o json
```

## Step 5 — Wire into CI

`vais eval results -o junit` emits standard JUnit XML. Any CI system that parses JUnit reports (GitHub Actions, Jenkins, GitLab CI, Azure DevOps) can consume it directly.

```bash
vais eval run support-bot-regression --wait
EVAL_RUN_ID=$(vais eval list --suite support-bot-regression --limit 1 -o json | jq -r '.items[0].evalRunId')
vais eval results "$EVAL_RUN_ID" -o junit > junit.xml
```

**GitHub Actions snippet:**

```yaml
- name: Run agent regression suite
  run: |
    vais eval run support-bot-regression --wait
    EVAL_RUN_ID=$(vais eval list --suite support-bot-regression --limit 1 -o json | jq -r '.items[0].evalRunId')
    vais eval results "$EVAL_RUN_ID" -o junit > junit.xml

- name: Publish test results
  uses: dorny/test-reporter@v1
  if: always()
  with:
    name: Agent eval
    path: junit.xml
    reporter: java-junit
```

Any failing case becomes a failing test in the CI report. A `metric-threshold` assertion that blows past its budget appears as a `<failure>` with the measured value in the message.

**Operational cost note:** a 3-case suite with one judge call is about 3 LLM calls per run. A 200-case suite with judge assertions on every case scales to ~200 LLM calls. Route the judge model through an `LlmGatewayConfig` with `LlmSemanticCacheMiddleware` — identical input × identical judge prompt hits the cache, dramatically reducing cost on repeated CI runs.

## Step 6 — Baseline and diff

Once a run is passing, you can pin it as a baseline and compare future runs against it.

Copy the passing run id into the suite manifest:

```yaml
spec:
  agentId: support-bot
  baseline:
    runId: 5641e831...        # ← your passing run id
  defaults:
    judgeModel: gpt-4o-mini
    timeout: "00:00:30"
  cases: ...
```

```bash
vais apply -f eval-support-bot.yaml
```

Now run again after changing the agent (for example, after updating the system prompt or swapping the model):

```bash
vais eval run support-bot-regression --wait
```

```
started evalRunId=1b7bf5e8...
  pass refund-policy
  fail billing-duplicate
  pass account-access
run completed
```

Diff the two runs:

```bash
vais eval diff 5641e831... 1b7bf5e8...
```

```
base:      5641e831...
candidate: 1b7bf5e8...

┌───────────────────┬──────┬───────────┬──────────────────────────────────┐
│ CASE              │ BASE │ CANDIDATE │ DELTA                            │
├───────────────────┼──────┼───────────┼──────────────────────────────────┤
│ refund-policy     │ pass │ pass      │ —                                │
│ billing-duplicate │ pass │ fail      │ judge-score ✗ (0.41 < 0.60)     │
│ account-access    │ pass │ pass      │ —                                │
└───────────────────┴──────┴───────────┴──────────────────────────────────┘
```

The diff shows exactly which assertions moved and by how much. Here the prompt change hurt the quality of the duplicate-charge response and doubled its latency — both surfaces visible in one command before the change ships.

`-o json` is available for machine-readable diff output (CI policy gates, Slack bots, etc.).

## Step 7 — Continuous production monitoring

Batch regression suites catch regressions before deployment. Continuous eval catches
them in production by scoring a statistical sample of real traffic without ever replaying
or altering it.

### How it works

Replace the `spec.cases:` block with a `spec.sampling:` block. The runtime subscribes
to the agent event bus and scores every run whose `hash(runId) / 2⁶⁴ < rate` — a
deterministic predicate that is identical on every silo, so N-silo deployments do not
produce N×-duplicate samples.

Each eval window (default: 1 h) accumulates samples in a single `eval_runs` row in
Postgres. When the window expires the grain closes the row with `status = Completed`
and opens the next one.

### Author a continuous suite

```yaml
apiVersion: vais.agents/v1
kind: EvalSuite
metadata:
  id: support-bot-continuous
  version: "1.0"
spec:
  agentId: support-bot
  sampling:
    rate: 0.01            # Score 1% of production runs
    windowDuration: 1h    # New eval_run row every hour
  assertions:
    - kind: no-turn-failed
    - kind: response-regex
      params:
        pattern: "\\S+"   # Response must be non-empty
```

> **Cost budget:** at 1% sampling × 100 rps × 1 judge call per sample = 1 judge call/s.
> For `judge-score` assertions, route the judge model through a `LlmGatewayConfig` with
> semantic-cache enabled to amortize repeat prompts:
>
> ```yaml
> spec.model.id: gpt-4o-judge
> ```
>
> The formula is `rate × traffic × judges_per_case × cost_per_call`. Start with
> `rate: 0.001` while validating your assertion chain; ramp up once the cost is justified.

### Apply and observe

```bash
vais apply -f support-bot-continuous.yaml

# List continuous runs (live, no --run-id needed)
vais eval list --source continuous

# Filter by suite
vais eval list --source continuous --suite support-bot-continuous
```

Example output:

```
EVAL RUN ID                          SUITE                    STATUS   WINDOW START          SAMPLED
ceval-support-bot-cont-20260518…     support-bot-continuous   Running  2026-05-18T14:00:00Z  12
ceval-support-bot-cont-20260518…     support-bot-continuous   Done     2026-05-18T13:00:00Z  47
```

### Grafana

Import `deploy/observability/grafana/dashboards/eval-continuous.json` into your Grafana
instance. Five panels ship out of the box:

| Panel | Metric | What to watch |
|---|---|---|
| Pass Rate Over Time | `vais_eval_continuous_cases_total` | Sustained drop below 95% = regression |
| Assertion Score (p50/p90) | `vais_eval_continuous_assertion_score` | Falling p90 on `judge-score` = drift |
| Sampled Runs Per Window | `vais_eval_continuous_window_sampled` | Zero = no traffic or sampler not running |
| Window Time Remaining | `vais_eval_continuous_window_seconds_remaining` | Near-zero = window about to rotate |
| Failed Cases/min | `vais_eval_continuous_cases_total{status="Fail"}` | Spike = investigate logs |

Live data appears within 30 s of the first sampled production run.

### Operational risks

- **Samples are point-in-time observations.** Runs that complete during a silo restart
  are not retroactively sampled. This is expected behaviour for `rate = 0.01` semantics.
- **Judge non-determinism.** `judge-score` assertions are not perfectly repeatable;
  treat sustained trends (5+ consecutive windows) rather than single-window spikes as
  signals. See `research/evaluation-harness-design-2026-05-16.md §9`.
- **Under-sampling.** When production traffic is below `1 / rate` rps, some windows get
  zero samples. The "Sampled Runs Per Window" panel surfaces this.

See `samples/EvalContinuousMonitor/` for a self-contained example.

## Clean up

```bash
vais delete eval-suites/support-bot-regression
vais delete eval-suites/support-bot-continuous
vais delete support-bot
```

## What you built

- A declarative `EvalSuite` manifest published via `vais apply` alongside the agent it tests.
- A 3-case suite using four assertion kinds: `no-turn-failed`, `response-regex`, `judge-score`, and `metric-threshold`.
- A JUnit XML pipeline gate that turns any assertion failure into a failing CI job.
- A baseline + diff workflow that surfaces regressions at the assertion level before they reach production.
- A continuous sampling suite that monitors production traffic at configurable rate with no agent replay.

## Next

- **[Intercept agent calls with an extension](intercept-agent-calls-with-an-extension.md)** — bind cross-cutting behaviour (logging, guardrails, memory enrichment) to any agent without touching its manifest; scope it, inspect the resolved chain, and remove it cleanly.
- **[Deep agent development](../deep-development/index.md)** — author plugin code when declarative YAML isn't enough.
- [Reference → EvalSuite manifest](../reference/manifest-schema.md#evalsuite) — full field reference for all ten assertion kinds.
- [Extensions → Author a custom assertion](../extensions/author-a-custom-assertion.md) — plug in your own `IEvalAssertionFactory` via DI.
- `samples/EvalRegression/` — self-contained sample: apply, run, and diff in one script.
- `samples/EvalContinuousMonitor/` — continuous eval sample with Grafana panel reference.
