# Author a container plugin in Go

You'll author a container plugin in Go — three HTTP endpoints, no SDK, no language-specific tooling. End state: a Go binary inside a Docker container, registered via `vais apply`, invoked through `vais invoke` like any other agent. Same shape works for any language that can serve HTTP.

## When this path?

Container plugins are right when:

- You want a language other than C# or Python.
- You want stronger isolation than in-process — the plugin runs in its own Docker container with `NetworkPolicy` and `securityContext` defaults applied by the runtime.
- You want to ship a plugin as an opaque image through your existing CI / registry / signing pipeline.

The IP-1 HTTP protocol is the entire contract. **No Go SDK ships** — the protocol is small enough that demonstrating it in Go also demonstrates how to write a plugin in Rust, Java, TypeScript, or anything else.

## What you implement

Three HTTP endpoints on whatever port your image exposes (the runtime reads `spec.port` from your plugin manifest):

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/health` | Liveness. Return `200` with any body when the container is ready. |
| `GET` | `/v1/metadata` | Identity. Return the handler `typeName` and the IP-1 API version. |
| `POST` | `/v1/invoke` | The actual turn. Receive `InvokeRequest`, return `InvokeResponse`. |

That's the entire contract. Streaming, opaque state round-tripping, and gateway-mediated LLM/tool calls layer on top using the same shape.

## Prerequisites

- Go 1.22+ installed locally.
- Docker.
- A running runtime ([DevOps section](../devops/index.md)) with container plugin support enabled (`VAIS_CONTAINER_PLUGINS_DIRECTORY` set and the Docker socket accessible). The local dev stack (`local-dev/dev.ps1`) is pre-configured.

## 1. The wire shapes

`GET /v1/metadata` response:

```json
{
  "handlerTypeName": "examples.echo.EchoPlugin",
  "targetApiVersion": "0.24"
}
```

`POST /v1/invoke` request body:

```json
{
  "agentId": "echo",
  "sessionId": "abc123",
  "messages": [
    {"role": "system", "content": "..."},
    {"role": "user",   "content": "Hello!"}
  ],
  "opaqueState": null,
  "llmGatewayUrl":  "http://runtime-internal/v1/llm",
  "toolGatewayUrl": "http://runtime-internal/v1/tools",
  "timeoutSeconds": 60,
  "context": { "runId": "...", "traceparent": "...", "callToken": "..." }
}
```

`POST /v1/invoke` 2xx response:

```json
{
  "assistantMessage": "echo: Hello!",
  "opaqueState": null
}
```

On failure, return a 4xx/5xx with:

```json
{
  "errorType": "InvalidInput",
  "errorMessage": "messages array was empty",
  "diagnosticTail": "(optional last N log lines)"
}
```

## 2. The Go implementation

`main.go`:

```go
package main

import (
    "encoding/json"
    "log"
    "net/http"
    "os"
    "strings"
)

const (
    apiVersion      = "0.24"
    handlerTypeName = "examples.echo.EchoPlugin"
)

type Message struct {
    Role    string `json:"role"`
    Content string `json:"content"`
}

type InvokeRequest struct {
    AgentID     string    `json:"agentId"`
    SessionID   string    `json:"sessionId"`
    Messages    []Message `json:"messages"`
    OpaqueState *string   `json:"opaqueState"`
}

type InvokeResponse struct {
    AssistantMessage string  `json:"assistantMessage"`
    OpaqueState      *string `json:"opaqueState"`
}

type Metadata struct {
    HandlerTypeName  string `json:"handlerTypeName"`
    TargetApiVersion string `json:"targetApiVersion"`
}

func main() {
    http.HandleFunc("/health", func(w http.ResponseWriter, _ *http.Request) {
        w.WriteHeader(http.StatusOK)
        _, _ = w.Write([]byte("ok"))
    })

    http.HandleFunc("/v1/metadata", func(w http.ResponseWriter, _ *http.Request) {
        w.Header().Set("Content-Type", "application/json")
        _ = json.NewEncoder(w).Encode(Metadata{
            HandlerTypeName:  handlerTypeName,
            TargetApiVersion: apiVersion,
        })
    })

    http.HandleFunc("/v1/invoke", func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost {
            http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
            return
        }

        var req InvokeRequest
        if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
            http.Error(w, "bad request", http.StatusBadRequest)
            return
        }

        // Find the latest user message and echo it back.
        var lastUser string
        for i := len(req.Messages) - 1; i >= 0; i-- {
            if req.Messages[i].Role == "user" {
                lastUser = strings.TrimSpace(req.Messages[i].Content)
                break
            }
        }

        w.Header().Set("Content-Type", "application/json")
        _ = json.NewEncoder(w).Encode(InvokeResponse{
            AssistantMessage: "echo: " + lastUser,
        })
    })

    port := os.Getenv("PORT")
    if port == "" {
        port = "8080"
    }
    log.Printf("listening on :%s", port)
    log.Fatal(http.ListenAndServe(":"+port, nil))
}
```

Three handlers, zero dependencies, ~60 lines. The echo logic is intentionally trivial — the point is the protocol shape, not the agent itself. A real plugin would call an LLM (via `request.llmGatewayUrl`) or do whatever work the agent's domain requires.

## 3. `go.mod`

```bash
go mod init examples/echo-plugin
```

No third-party deps — Go's standard library covers HTTP + JSON.

## 4. Dockerfile

```dockerfile
FROM golang:1.22-alpine AS build
WORKDIR /src
COPY go.mod ./
COPY main.go ./
RUN CGO_ENABLED=0 go build -ldflags="-s -w" -o /out/plugin ./...

FROM scratch
COPY --from=build /out/plugin /plugin
USER 65532:65532
EXPOSE 8080
ENTRYPOINT ["/plugin"]
```

Multi-stage build → ~10 MB static binary on a `scratch` base. The runtime's `DockerContainerSupervisor` enforces non-root, read-only rootfs, dropped capabilities, and `no-new-privileges` regardless — the `USER` directive here matches what the supervisor expects.

## 5. `plugin.yaml`

```yaml
apiVersion: vais.agents/v1
kind: ContainerPlugin
metadata:
  id: echo
  version: "1.0"
  description: Trivial Go container plugin that echoes the last user message.
spec:
  image: echo-plugin:1.0
  port: 8080
  build:
    context: .
    dockerfile: Dockerfile
```

## 6. Apply and invoke

```bash
vais apply -f plugin.yaml
# Building echo-plugin:1.0 from .
# ✓ built echo-plugin:1.0
# echo created (container-plugin, version 1.0)

vais plugin-status
# NAME   KIND        IMAGE             TOPOLOGY    STATE   API VERSION   HANDLERS / TOOLS
# echo   container   echo-plugin:1.0   standalone  ready   0.24          examples.echo.EchoPlugin
```

Apply an agent manifest:

```yaml
# agent.yaml
apiVersion: vais.agents/v1
kind: Agent
metadata:
  id: echo
  version: "1.0"
spec:
  handler:
    typeName: examples.echo.EchoPlugin    # must match Metadata.handlerTypeName
  protocols:
    - kind: Http
```

```bash
vais apply -f agent.yaml
vais invoke echo --text "Hello, plugin!"
# echo: Hello, plugin!
```

A complete, working container plugin in Go — no SDK, no language-specific tooling beyond `go build`.

## Cleanup

```bash
vais delete echo                                              # remove the agent
curl -X DELETE http://localhost:8080/v1/container-plugins/echo  # evict the plugin
```

The second command stops the plugin container and removes it from the registry. A `vais plugin-delete` CLI shorthand will be added in a future release.

## Kubernetes NetworkPolicy (default-on)

When you deploy with the `vais-plugin` Helm chart, a `NetworkPolicy` is created automatically (`networkPolicy.enabled: true` by default). It:

- **Ingress**: allows traffic only from the runtime service pod (identified by namespace + pod label).
- **Egress**: allows DNS on port 53 (kube-dns) and traffic to the runtime service port only.

To opt out (e.g. for local development):

```bash
helm upgrade my-plugin /path/to/vais-plugin --set networkPolicy.enabled=false
```

To add extra egress rules (e.g. to reach an external database):

```yaml
# values-extra.yaml
networkPolicy:
  extraEgress:
    - to:
        - ipBlock:
            cidr: 10.0.0.0/8
      ports:
        - port: 5432
          protocol: TCP
```

```bash
helm upgrade my-plugin /path/to/vais-plugin -f values-extra.yaml
```

The policy is a P12 convention, not a hard guarantee — actual network isolation depends on your CNI plugin supporting `NetworkPolicy`. Calico, Cilium, and Weave support it; the default `kindnet` in kind clusters does not.

## Going further

The echo plugin doesn't call an LLM or tools. A production plugin would:

- **Call the runtime's LLM gateway** at `request.llmGatewayUrl` instead of an LLM provider directly. This ensures the runtime's middleware chain (rate limiting, OTel tracing, Langfuse enrichment, token-budget enforcement) applies. The gateway is the only LLM path per the runtime's [P4 architectural principle](../concepts/control-plane.md).
- **Call tools** via the MCP gateway at `request.toolGatewayUrl` for the same reason.
- **Round-trip state** through `opaqueState` — set it on the response, receive it on the next request. The grain persists it; you don't.
- **Authenticate gateway calls** with the `callToken` from `request.context`. The token is short-lived and scoped to the `(runId, agentId)` pair.

A reference implementation in Python that shows the full pattern: [`samples/PluginAgentLangGraphResearcherLive/`](../../samples/PluginAgentLangGraphResearcherLive/). The patterns translate directly — call `llmGatewayUrl` with the gateway's HTTP shape rather than the provider's.

## What you built

- A complete container plugin in Go using only the standard library.
- A demonstration that the IP-1 HTTP protocol is the entire contract — language-neutral by construction.
- A baseline you can extend with LLM calls (via the gateway), tool calls (via the gateway), or state round-tripping.

## Next

- **[Extensions](../extensions/index.md)** — customize the runtime's middleware seams.
- [Concepts → Polyglot plugins](../concepts/polyglot-plugins.md) — protocol, lifecycle, the IP-1 spec.
- [Sample → `samples/quickstart-go-plugin/`](../../samples/quickstart-go-plugin/) — the working version of this tutorial as a single sample directory.
- [P12 plugin sandbox contract](../concepts/control-plane.md) — security defaults the runtime applies to every container plugin.
