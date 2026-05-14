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

# Copy all sources. A single wildcard COPY is simpler and always correct as new
# projects are added; the NuGet restore cache is preserved by the layer above.
COPY src/ src/

RUN dotnet restore src/Vais.Agents.Runtime.Host/Vais.Agents.Runtime.Host.csproj

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
