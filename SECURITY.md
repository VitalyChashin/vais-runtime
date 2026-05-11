# Security

Vais.Agents is in Phase 1 (pre-alpha). The library and runtime are usable for experimentation but not production-hardened. Take security reports seriously regardless of release status — but understand the constraints stated below.

## Reporting a vulnerability

**Do not open a public issue for security vulnerabilities.** Use GitHub's Private Vulnerability Reporting on this repository:

1. Go to the repository's **Security** tab.
2. Click **Report a vulnerability**.
3. Fill in the form.

That channel is private to the maintainers and tracked in the same way as a public issue, but invisible to other users until the maintainers publish an advisory.

If Private Vulnerability Reporting is not yet enabled on a fork or mirror, or you cannot use GitHub, open a minimal public issue titled `[security] please contact me privately` — no details — and a maintainer will reach out.

### What to include

- Affected package(s) and version / commit SHA.
- A clear description of the vulnerability and its impact.
- Reproduction steps. A minimal failing test or repro project is ideal.
- Suggested remediation, if you have one.
- Whether you intend to publish independently and when.

## Disclosure timeline

| Stage | Target |
|---|---|
| Acknowledgement of report | 5 working days |
| Initial assessment + fix plan | 10 working days |
| Fix released (severity-dependent) | Best effort — typically days for high severity, weeks for low |
| Public advisory published | After the fix is available, coordinated with the reporter |

We aim to give the reporter credit in the GitHub Security Advisory unless they request otherwise.

## Supported versions

| Version | Supported |
|---|---|
| `main` | ✅ Active development. Security fixes land here first. |
| Tagged `0.X.0-preview` releases | ❌ No back-porting. Upgrade to a current `main` build. |

There is no long-term support branch during Phase 1.

## Scope

In scope:

- Authentication / authorisation bypass in the HTTP control plane, A2A endpoints, MCP endpoints, or the Kubernetes operator.
- Token, key, or credential leakage via logs, traces, error messages, or persisted state.
- Path traversal, injection (SQL / command / template), or unsafe deserialisation reachable from an untrusted input boundary.
- Cryptographic misuse in `Vais.Agents.Identity.Oidc` or any package that handles credentials.
- Cross-tenant data leakage through grain state, event bus, or persistence layers.

Out of scope:

- LLM-side prompt-injection that does not breach a library invariant. Prompt injection is an inherent property of LLMs; mitigation belongs in caller-supplied guardrails and the gateway middleware chain, both of which are extension points the library exposes.
- Denial-of-service via legitimate-but-expensive workloads (large context windows, deep graph fan-out). Use the rate-limiting and budget middleware in `Vais.Agents.Gateways.Governance` to defend against these.
- Bugs that do not breach a security boundary — file as a regular [bug report](.github/ISSUE_TEMPLATE/bug_report.md).

## Transitive dependencies

If the vulnerability is in a transitive dependency (Semantic Kernel, Microsoft Agent Framework, Microsoft.Extensions.AI, OpenAI .NET client, Orleans, etc.), please report it to the upstream project first. We track upstream advisories via Dependabot once configured.

If you are unsure whether an issue is in Vais.Agents or an upstream package, report it here and we will route it.
