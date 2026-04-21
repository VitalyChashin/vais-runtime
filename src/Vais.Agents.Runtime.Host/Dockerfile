# syntax=docker/dockerfile:1.7

# Multi-stage build for the Vais.Agents runtime container (v0.16 Pillar A).
# Stage 1 restores + publishes the host assembly; stage 2 runs it on a minimal
# aspnet-alpine base as a non-root user. The BASE_IMAGE arg exists so Pillar F
# polish can flip to the chiseled base with a single build argument.

ARG DOTNET_VERSION=9.0
ARG BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
WORKDIR /src

# Copy solution-level MSBuild files first to maximise layer caching.
COPY Directory.Build.props Directory.Packages.props NuGet.config* ./

# Bring in the csproj files referenced by the host's publish graph so `dotnet
# restore` can resolve the package + project graph without pulling the sources.
COPY src/Vais.Agents.Abstractions/Vais.Agents.Abstractions.csproj                                       src/Vais.Agents.Abstractions/
COPY src/Vais.Agents.Core/Vais.Agents.Core.csproj                                                       src/Vais.Agents.Core/
COPY src/Vais.Agents.Control.Abstractions/Vais.Agents.Control.Abstractions.csproj                       src/Vais.Agents.Control.Abstractions/
COPY src/Vais.Agents.Control.InProcess/Vais.Agents.Control.InProcess.csproj                             src/Vais.Agents.Control.InProcess/
COPY src/Vais.Agents.Control.Manifests.Json/Vais.Agents.Control.Manifests.Json.csproj                   src/Vais.Agents.Control.Manifests.Json/
COPY src/Vais.Agents.Control.Manifests.Yaml/Vais.Agents.Control.Manifests.Yaml.csproj                   src/Vais.Agents.Control.Manifests.Yaml/
COPY src/Vais.Agents.Control.Http.Server/Vais.Agents.Control.Http.Server.csproj                         src/Vais.Agents.Control.Http.Server/
COPY src/Vais.Agents.Control.Policy.Opa/Vais.Agents.Control.Policy.Opa.csproj                           src/Vais.Agents.Control.Policy.Opa/
COPY src/Vais.Agents.Hosting.Orleans/Vais.Agents.Hosting.Orleans.csproj                                 src/Vais.Agents.Hosting.Orleans/
COPY src/Vais.Agents.Persistence.Redis/Vais.Agents.Persistence.Redis.csproj                             src/Vais.Agents.Persistence.Redis/
COPY src/Vais.Agents.Persistence.Postgres/Vais.Agents.Persistence.Postgres.csproj                       src/Vais.Agents.Persistence.Postgres/
COPY src/Vais.Agents.Observability.OpenTelemetry/Vais.Agents.Observability.OpenTelemetry.csproj         src/Vais.Agents.Observability.OpenTelemetry/
COPY src/Vais.Agents.Observability.Langfuse/Vais.Agents.Observability.Langfuse.csproj                   src/Vais.Agents.Observability.Langfuse/
COPY src/Vais.Agents.Runtime.Host/Vais.Agents.Runtime.Host.csproj                                       src/Vais.Agents.Runtime.Host/

RUN dotnet restore src/Vais.Agents.Runtime.Host/Vais.Agents.Runtime.Host.csproj

# Copy the rest of the sources the host transitively depends on.
COPY src/Vais.Agents.Abstractions/                       src/Vais.Agents.Abstractions/
COPY src/Vais.Agents.Core/                               src/Vais.Agents.Core/
COPY src/Vais.Agents.Control.Abstractions/               src/Vais.Agents.Control.Abstractions/
COPY src/Vais.Agents.Control.InProcess/                  src/Vais.Agents.Control.InProcess/
COPY src/Vais.Agents.Control.Manifests.Json/             src/Vais.Agents.Control.Manifests.Json/
COPY src/Vais.Agents.Control.Manifests.Yaml/             src/Vais.Agents.Control.Manifests.Yaml/
COPY src/Vais.Agents.Control.Http.Server/                src/Vais.Agents.Control.Http.Server/
COPY src/Vais.Agents.Control.Policy.Opa/                 src/Vais.Agents.Control.Policy.Opa/
COPY src/Vais.Agents.Hosting.Orleans/                    src/Vais.Agents.Hosting.Orleans/
COPY src/Vais.Agents.Persistence.Redis/                  src/Vais.Agents.Persistence.Redis/
COPY src/Vais.Agents.Persistence.Postgres/               src/Vais.Agents.Persistence.Postgres/
COPY src/Vais.Agents.Observability.OpenTelemetry/        src/Vais.Agents.Observability.OpenTelemetry/
COPY src/Vais.Agents.Observability.Langfuse/             src/Vais.Agents.Observability.Langfuse/
COPY src/Vais.Agents.Runtime.Host/                       src/Vais.Agents.Runtime.Host/

RUN dotnet publish src/Vais.Agents.Runtime.Host/Vais.Agents.Runtime.Host.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

FROM ${BASE_IMAGE} AS runtime
WORKDIR /app

# Non-root uid/gid matches the distroless/chiseled convention so a flip to the
# chiseled base in Pillar F is a no-op for the volume + PSP / PSS side.
RUN addgroup -S -g 65532 vais \
    && adduser -S -u 65532 -G vais vais \
    && mkdir -p /var/lib/vais/plugins \
    && chown -R vais:vais /var/lib/vais

USER vais:vais

COPY --from=build --chown=vais:vais /app/publish ./

VOLUME ["/var/lib/vais/plugins"]
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
    CMD wget -q -O- http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Vais.Agents.Runtime.Host.dll"]
