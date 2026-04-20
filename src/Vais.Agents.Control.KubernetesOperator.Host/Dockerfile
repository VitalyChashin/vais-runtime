# syntax=docker/dockerfile:1.7

# Multi-stage build for the Vais.Agents Kubernetes operator.
# Stage 1 restores + publishes the host assembly; stage 2 runs it on a
# minimal aspnet-alpine base as a non-root user.

ARG DOTNET_VERSION=9.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
WORKDIR /src

# Copy solution-level MSBuild files first to maximise layer caching.
COPY Directory.Build.props Directory.Packages.props NuGet.config* ./

# Bring in only the project files referenced by the host publish graph.
COPY src/Vais.Agents.Abstractions/Vais.Agents.Abstractions.csproj                                       src/Vais.Agents.Abstractions/
COPY src/Vais.Agents.Control.Abstractions/Vais.Agents.Control.Abstractions.csproj                       src/Vais.Agents.Control.Abstractions/
COPY src/Vais.Agents.Control.Http.Client/Vais.Agents.Control.Http.Client.csproj                         src/Vais.Agents.Control.Http.Client/
COPY src/Vais.Agents.Control.KubernetesOperator/Vais.Agents.Control.KubernetesOperator.csproj           src/Vais.Agents.Control.KubernetesOperator/
COPY src/Vais.Agents.Control.KubernetesOperator.Host/Vais.Agents.Control.KubernetesOperator.Host.csproj src/Vais.Agents.Control.KubernetesOperator.Host/

RUN dotnet restore src/Vais.Agents.Control.KubernetesOperator.Host/Vais.Agents.Control.KubernetesOperator.Host.csproj

# Copy the rest of the sources the host depends on.
COPY src/Vais.Agents.Abstractions/                          src/Vais.Agents.Abstractions/
COPY src/Vais.Agents.Control.Abstractions/                  src/Vais.Agents.Control.Abstractions/
COPY src/Vais.Agents.Control.Http.Client/                   src/Vais.Agents.Control.Http.Client/
COPY src/Vais.Agents.Control.KubernetesOperator/            src/Vais.Agents.Control.KubernetesOperator/
COPY src/Vais.Agents.Control.KubernetesOperator.Host/       src/Vais.Agents.Control.KubernetesOperator.Host/

RUN dotnet publish src/Vais.Agents.Control.KubernetesOperator.Host/Vais.Agents.Control.KubernetesOperator.Host.csproj \
        -c Release \
        --no-restore \
        -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS runtime
WORKDIR /app

# Non-root uid/gid chosen to match the distroless-aspnet convention.
RUN addgroup -S -g 65532 vais && adduser -S -u 65532 -G vais vais
USER vais:vais

COPY --from=build --chown=vais:vais /app/publish ./

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=3s --retries=3 \
    CMD wget -q -O- http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "Vais.Agents.Control.KubernetesOperator.Host.dll"]
