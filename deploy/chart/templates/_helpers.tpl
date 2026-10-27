{{/*
Names. Every object is named after the release: <release> (web), <release>-postgres, <release>-geometry, ...
The infra prod environment uses the release name `blocwerk`, which keeps the compose-era service names stable
(Eunomia sends telemetry to blocwerk-aspire:18889; backups dump from blocwerk-postgres).
*/}}
{{- define "blocwerk.fullname" -}}
{{- .Release.Name | trunc 40 | trimSuffix "-" -}}
{{- end -}}

{{- define "blocwerk.selectorLabels" -}}
app.kubernetes.io/name: blocwerk
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{- define "blocwerk.labels" -}}
{{ include "blocwerk.selectorLabels" . }}
app.kubernetes.io/version: {{ .root.Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version | replace "+" "_" }}
{{- end -}}

{{/* image: {repository, tag, digest} plus a default tag. Usage: include "blocwerk.image" (list .Values.web.image "1.2.3") */}}
{{- define "blocwerk.image" -}}
{{- $img := index . 0 -}}
{{- $default := index . 1 -}}
{{- if $img.digest -}}
{{- printf "%s@%s" $img.repository $img.digest -}}
{{- else -}}
{{- printf "%s:%s" $img.repository (default $default $img.tag | toString) -}}
{{- end -}}
{{- end -}}

{{/* A map of plain environment variables; empty values are left out (see values.yaml). */}}
{{- define "blocwerk.envMap" -}}
{{- $items := list -}}
{{- range $name, $value := . -}}
{{- if ne (toString $value) "" -}}
{{- $items = append $items (printf "- name: %s\n  value: %s" $name ($value | toString | quote)) -}}
{{- end -}}
{{- end -}}
{{- join "\n" $items -}}
{{- end -}}

{{/* Placement and pull secrets, shared by every pod of the release. */}}
{{- define "blocwerk.placement" -}}
{{- with .Values.imagePullSecrets }}
imagePullSecrets:
  {{- toYaml . | nindent 2 }}
{{- end }}
{{- with .Values.nodeSelector }}
nodeSelector:
  {{- toYaml . | nindent 2 }}
{{- end }}
{{- with .Values.tolerations }}
tolerations:
  {{- toYaml . | nindent 2 }}
{{- end }}
{{- end -}}

{{/* The pod options of every workload pod: no kube API token, plus the shared placement. */}}
{{- define "blocwerk.podOptions" -}}
automountServiceAccountToken: false
{{- with include "blocwerk.placement" . | trim }}{{ nindent 0 . }}{{ end }}
{{- end -}}

{{/*
Hook annotations for the deploy gate's objects: ArgoCD (phase + wave) and, for plain `helm upgrade`, pre-upgrade with
a weight. The objects stay until the next sync (BeforeHookCreation), so the gate's log can be read afterwards.
*/}}
{{- define "blocwerk.hookAnnotations" -}}
argocd.argoproj.io/hook: {{ .phase }}
argocd.argoproj.io/sync-wave: {{ .wave | quote }}
argocd.argoproj.io/hook-delete-policy: BeforeHookCreation
helm.sh/hook: pre-upgrade
helm.sh/hook-weight: {{ .wave | quote }}
helm.sh/hook-delete-policy: before-hook-creation
{{- end -}}

{{/* The security context of every container that needs nothing special. */}}
{{- define "blocwerk.restrictedContainer" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: {{ . }}
capabilities:
  drop: [ALL]
{{- end -}}

{{/*
Rollout fingerprint of a pod spec: what the deploy gate compares between the running pods and the sync target.
A change of image, environment, volumes or resources changes it; labels and the chart version do not.
*/}}
{{- define "blocwerk.fingerprint" -}}
{{- . | sha256sum | trunc 16 -}}
{{- end -}}

{{- define "blocwerk.postgresHost" -}}
{{- if .Values.postgres.enabled -}}
{{- printf "%s-postgres" (include "blocwerk.fullname" .) -}}
{{- else -}}
{{- required "postgres.external.host is required when postgres.enabled is false" .Values.postgres.external.host -}}
{{- end -}}
{{- end -}}

{{- define "blocwerk.postgresPort" -}}
{{- if .Values.postgres.enabled -}}5432{{- else -}}{{ .Values.postgres.external.port }}{{- end -}}
{{- end -}}

{{/* CAPTURE_MAX_* for the workers, from the shared capture limits. */}}
{{- define "blocwerk.workerCaptureEnv" -}}
{{- include "blocwerk.envMap" (dict "CAPTURE_MAX_PHOTOS" .Values.capture.maxPhotos "CAPTURE_MAX_VIDEO_FRAMES" .Values.capture.maxVideoFrames) -}}
{{- end -}}
