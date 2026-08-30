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

{{/*
The web image's Content-Security-Policy `connect-src`, DERIVED from
web.oidcAuthority unless the operator states one explicitly.

Why derived rather than restated: connect-src has to name the Keycloak origin
or sign-in fails at the first discovery fetch (oidc-client-ts uses XHR), and
that origin is already stated one line above as web.oidcAuthority. Two values
that must agree, maintained by hand, is a drift bug with a schedule. So the
common case needs one value, and the override exists for the deployments that
genuinely need more origins in the list (an OTLP collector, a second IdP).

An explicit web.csp.connectSrc is used verbatim — including whatever the
operator did or did not put in it. That is the point of an override.
*/}}
{{- define "rocketwiki.web.cspConnectSrc" -}}
{{- if .Values.web.csp.connectSrc -}}
{{- .Values.web.csp.connectSrc -}}
{{- else -}}
{{- $origin := include "rocketwiki.web.oidcOrigin" . -}}
{{- printf "'self' %s" $origin -}}
{{- end -}}
{{- end -}}

{{/*
The ORIGIN of web.oidcAuthority — scheme + host + port, no path. A CSP source
is an origin: "https://keycloak.internal/realms/rocketwiki" as written would
make the whole directive silently useless, since CSP would match it as a path
prefix nobody ever requests.

Fails the render rather than producing an empty origin. values.schema.json
already pins the shape, so reaching this is a schema that was edited without
this template — which is exactly when a silent empty string would be worst: it
renders `connect-src 'self' `, sign-in breaks, and nothing says why.
*/}}
{{- define "rocketwiki.web.oidcOrigin" -}}
{{- $url := urlParse (.Values.web.oidcAuthority | default "") -}}
{{- if or (not $url.scheme) (not $url.host) -}}
{{- fail (printf "web.oidcAuthority must be an http:// or https:// URL (got %q). It is the Keycloak realm URL the web image was BUILT with (--build-arg VITE_OIDC_AUTHORITY), and its origin is what the CSP's connect-src must name." .Values.web.oidcAuthority) -}}
{{- end -}}
{{- printf "%s://%s" $url.scheme $url.host -}}
{{- end -}}
