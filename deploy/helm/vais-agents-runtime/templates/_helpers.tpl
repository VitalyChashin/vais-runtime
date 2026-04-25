{{/*
Expand the name of the chart.
*/}}
{{- define "vais-agents-runtime.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a fully qualified app name.
*/}}
{{- define "vais-agents-runtime.fullname" -}}
{{- if .Values.fullnameOverride }}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- $name := default .Chart.Name .Values.nameOverride }}
{{- if contains $name .Release.Name }}
{{- .Release.Name | trunc 63 | trimSuffix "-" }}
{{- else }}
{{- printf "%s-%s" .Release.Name $name | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}
{{- end }}

{{/*
Standard labels attached to every resource.
*/}}
{{- define "vais-agents-runtime.labels" -}}
app.kubernetes.io/name: {{ include "vais-agents-runtime.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/component: runtime
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- with .Values.labels }}
{{ toYaml . }}
{{- end }}
{{- end }}

{{/*
Selector labels for the Deployment + Service.
*/}}
{{- define "vais-agents-runtime.selectorLabels" -}}
app.kubernetes.io/name: {{ include "vais-agents-runtime.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
ServiceAccount name — honours explicit override in values, else derives from fullname.
*/}}
{{- define "vais-agents-runtime.serviceAccountName" -}}
{{- if .Values.serviceAccount.create }}
{{- default (include "vais-agents-runtime.fullname" .) .Values.serviceAccount.name }}
{{- else }}
{{- default "default" .Values.serviceAccount.name }}
{{- end }}
{{- end }}

{{/*
ConfigMap name for the auto-generated OPA policy bundle (used only when
opa.enabled=true and opa.configMapName is empty).
*/}}
{{- define "vais-agents-runtime.opaPolicyConfigMapName" -}}
{{- if .Values.opa.configMapName }}
{{- .Values.opa.configMapName }}
{{- else }}
{{- printf "%s-opa-policy" (include "vais-agents-runtime.fullname" .) | trunc 63 | trimSuffix "-" }}
{{- end }}
{{- end }}

{{/*
True when the chart should spin up a pod-local OPA sidecar. Distinct from
`opa.enabled` because "enabled + baseUrl set" means "external OPA; no
sidecar".
*/}}
{{- define "vais-agents-runtime.opaSidecar" -}}
{{- if and .Values.opa.enabled (not .Values.opa.baseUrl) -}}true{{- else -}}false{{- end -}}
{{- end }}

{{/*
Effective OPA base URL emitted as the VAIS_OPA_BASEURL env var. Falls back to
the sidecar loopback when no external URL is supplied.
*/}}
{{- define "vais-agents-runtime.opaBaseUrl" -}}
{{- if .Values.opa.baseUrl -}}
{{- .Values.opa.baseUrl }}
{{- else -}}
http://localhost:8181
{{- end -}}
{{- end }}

{{/*
Name of the env var that carries the clustering connection string. Redis →
VAIS_REDIS_CONNECTION; Postgres → VAIS_POSTGRES_CONNECTION. The runtime host
reads whichever matches `VAIS_CLUSTERING_BACKEND`.
*/}}
{{- define "vais-agents-runtime.clusteringEnvName" -}}
{{- if eq .Values.clustering.backend "postgres" -}}VAIS_POSTGRES_CONNECTION{{- else -}}VAIS_REDIS_CONNECTION{{- end -}}
{{- end }}

{{/*
True when the chart should run the OPA sidecar in bundle-server polling mode.
Requires sidecar mode (opa.enabled=true and opa.baseUrl empty) PLUS
opa.bundle.enabled=true.
*/}}
{{- define "vais-agents-runtime.opaBundleMode" -}}
{{- if and (eq (include "vais-agents-runtime.opaSidecar" .) "true") .Values.opa.bundle.enabled -}}true{{- else -}}false{{- end -}}
{{- end }}

{{/*
Name of the ConfigMap that holds the OPA server config.yaml (bundle mode).
*/}}
{{- define "vais-agents-runtime.opaConfigMapName" -}}
{{- printf "%s-opa-config" (include "vais-agents-runtime.fullname" .) | trunc 63 | trimSuffix "-" }}
{{- end }}
