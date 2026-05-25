# syntax=docker/dockerfile:1
# Full-stack development Dockerfile: .NET runtime (built from source) + live LangGraph Python plugin.
# Build context: repo root (two levels up from this file's directory).
# Used by docker-compose.yml in this directory.
#
# For production with a published runtime image use langgraph-researcher-live/Dockerfile.overlay instead.

ARG DOTNET_VERSION=10.0

# ── Stage 1: Build the .NET runtime ──────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS dotnet-build
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY NuGet.config* ./
COPY src/ src/

RUN dotnet restore src/Vais.Agents.Runtime.Host/Vais.Agents.Runtime.Host.csproj
RUN dotnet publish src/Vais.Agents.Runtime.Host/Vais.Agents.Runtime.Host.csproj \
    -c Release --no-restore -o /app/publish

# ── Stage 2: Build the Python plugin venv on Linux ───────────────────────────
# Runs inside Linux, so the produced .venv is Linux-native — safe to COPY into
# the final stage regardless of the developer's host OS.
FROM ghcr.io/astral-sh/uv:python3.12-bookworm-slim AS python-builder
WORKDIR /plugin

# vais-agent-sdk — replace with `uv pip install vais-agent-sdk>=0.24` once published to PyPI.
COPY samples/python-agent-sdk /sdk
COPY samples/PluginAgentLangGraphResearcherLive/langgraph-researcher-live/src src

RUN uv venv .venv \
    && uv pip install --python .venv/bin/python /sdk \
    && uv pip install --python .venv/bin/python \
       "langgraph>=1.0,<2" "langchain-openai>=0.2,<1" "pydantic>=2.8,<3"

# ── Stage 3: Final image — .NET runtime + Python plugin ──────────────────────
# Uses the default .NET 10 aspnet base (Ubuntu 24.04); `apt-get install python3.12`
# provides /usr/bin/python3.12 — the same path referenced by the python3.12 .venv
# built in the python-builder stage above. (.NET 10 dropped the Debian base images
# that previously supplied python3.11; Ubuntu 24.04's system Python is 3.12.)
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3.12 \
    && ln -sf /usr/bin/python3.12 /usr/local/bin/python3.12 \
    && ln -sf /usr/bin/python3.12 /usr/local/bin/python3 \
    && ln -sf /usr/bin/python3.12 /usr/local/bin/python \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd -r -g 65532 vais \
    && useradd -r -u 65532 -g vais vais \
    && mkdir -p /var/lib/vais/plugins \
    && mkdir -p /var/lib/vais/python-plugins/langgraph-researcher-live \
    && chown -R vais:vais /var/lib/vais

# .NET runtime binaries
COPY --from=dotnet-build --chown=vais:vais /app/publish ./

# Python plugin: venv, source, and descriptor files
COPY --from=python-builder --chown=vais:vais /plugin/.venv \
     /var/lib/vais/python-plugins/langgraph-researcher-live/.venv
COPY --from=python-builder --chown=vais:vais /plugin/src \
     /var/lib/vais/python-plugins/langgraph-researcher-live/src
COPY --chown=vais:vais \
     samples/PluginAgentLangGraphResearcherLive/langgraph-researcher-live/plugin.yaml \
     /var/lib/vais/python-plugins/langgraph-researcher-live/plugin.yaml
COPY --chown=vais:vais \
     samples/PluginAgentLangGraphResearcherLive/langgraph-researcher-live/pyproject.toml \
     /var/lib/vais/python-plugins/langgraph-researcher-live/pyproject.toml

ENV VAIS_PYTHON_PLUGINS_DIRECTORY=/var/lib/vais/python-plugins
# OPENAI_API_KEY is passed at runtime — see docker-compose.yml or docker run -e

VOLUME ["/var/lib/vais/plugins"]
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
    CMD wget -q -O- http://localhost:8080/healthz || exit 1

USER vais:vais
ENTRYPOINT ["dotnet", "Vais.Agents.Runtime.Host.dll"]
