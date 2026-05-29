# syntax=docker/dockerfile:1.7

# Multi-stage build for the Vais.Agents code-mode sandbox sidecar (ScriptRuntime).
# Stage 1 restores + publishes the host assembly; stage 2 runs it on a minimal
# aspnet-alpine base as a non-root user. The container carries no writable state —
# it runs cleanly under the supervisor's read-only rootfs + tmpfs /tmp hardening.

ARG DOTNET_VERSION=10.0
ARG BASE_IMAGE=mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
WORKDIR /src

# Solution-level MSBuild files first to maximise layer caching.
COPY Directory.Build.props Directory.Packages.props NuGet.config* ./

# Only the source tree is needed — this project does not embed contracts/ artifacts.
COPY src/ src/

RUN dotnet restore src/Vais.Agents.ScriptRuntime.Host/Vais.Agents.ScriptRuntime.Host.csproj

RUN dotnet publish src/Vais.Agents.ScriptRuntime.Host/Vais.Agents.ScriptRuntime.Host.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

FROM ${BASE_IMAGE} AS runtime
WORKDIR /app

# Non-root uid/gid matches the distroless/chiseled convention.
RUN addgroup -S -g 65532 vais \
    && adduser -S -u 65532 -G vais vais

USER vais:vais

COPY --from=build --chown=vais:vais /app/publish ./

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
    CMD wget -q -O- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Vais.Agents.ScriptRuntime.Host.dll"]
