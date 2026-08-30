{{/*
Shared helpers. Nothing clever: names, labels, and the one piece of real
logic — image references that honor the air-gapped registry mirror
(global.imageRegistry, design.md §15 "Air-gapped install").
*/}}

{{- define "rocketwiki.name" -}}
{{- .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{/*
Release-scoped base name. If the release is already called "rocketwiki",
don't produce "rocketwiki-rocketwiki".
*/}}
{{- define "rocketwiki.fullname" -}}
{{- if contains .Chart.Name .Release.Name -}}
{{- .Release.Name | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name .Chart.Name | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{/* Standard label set; component label is added per-template. */}}
{{- define "rocketwiki.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/name: {{ include "rocketwiki.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{/* Selector labels must stay stable across chart versions — never add
     version/chart labels here or every upgrade breaks the Deployment
     selector (which is immutable). */}}
{{- define "rocketwiki.selectorLabels" -}}
app.kubernetes.io/name: {{ include "rocketwiki.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
{{- end -}}

{{/*
Image reference: prefix with global.imageRegistry when set (registry-mirror
offline path), otherwise use the name verbatim (which is what a
`k3s ctr images import`-preloaded image is found by).
Usage: {{ include "rocketwiki.image" (dict "global" .Values.global "image" .Values.api.image) }}
The dict is required, not stylistic: this template reads .global and .image, so
passing .Values.api.image directly (as this line used to show) renders a bare ":".
*/}}
{{- define "rocketwiki.image" -}}
{{- $registry := .global.imageRegistry | default "" -}}
{{- if $registry -}}
{{- printf "%s/%s:%s" $registry .image.repository .image.tag -}}
{{- else -}}
{{- printf "%s:%s" .image.repository .image.tag -}}
{{- end -}}
{{- end -}}

{{/* imagePullSecrets block, shared by all pod specs. */}}
{{- define "rocketwiki.imagePullSecrets" -}}
{{- with .Values.imagePullSecrets }}
imagePullSecrets:
{{- toYaml . | nindent 2 }}
{{- end }}
{{- end -}}
