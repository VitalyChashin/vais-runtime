{{/*
Expand the name of the chart.
*/}}
{{- define "vais-agents-operator.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" }}
{{- end }}

{{/*
Create a fully qualified app name.
*/}}
{{- define "vais-agents-operator.fullname" -}}
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
{{- define "vais-agents-operator.labels" -}}
app.kubernetes.io/name: {{ include "vais-agents-operator.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
{{- with .Values.labels }}
{{ toYaml . }}
{{- end }}
{{- end }}

{{/*
Selector labels for the Deployment.
*/}}
{{- define "vais-agents-operator.selectorLabels" -}}
app.kubernetes.io/name: {{ include "vais-agents-operator.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end }}

{{/*
Name of the ServiceAccount the operator runs as.
*/}}
{{- define "vais-agents-operator.serviceAccountName" -}}
{{- printf "%s-operator" (include "vais-agents-operator.fullname" .) | trunc 63 | trimSuffix "-" }}
{{- end }}
