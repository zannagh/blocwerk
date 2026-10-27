{{/*
The two Python compute workers (wall-geometry, splat-cpu). Both keep their job queue in memory: one replica,
replaced only after the compute gate saw them idle. The templates below take one entry
of blocwerk.worker.list plus `root` (the chart context) and `v` (the worker's values).
*/}}
{{/* The enabled workers as a list of dicts ready for the templates below; empty when none is enabled. */}}
{{- define "blocwerk.worker.list" -}}
{{- $all := list
  (dict "component" "geometry" "key" "geometry" "port" 8000 "apiKey" "COMPUTE_API_KEY" "fixedEnv" dict)
  (dict "component" "splat-cpu" "key" "splatCpu" "port" 8100 "apiKey" "SPLAT_API_KEY" "fixedEnv" (dict "SPLAT_WORKER_MODE" "cpu")) -}}
{{- $list := list -}}
{{- range $all -}}
{{- if (index $.Values .key).enabled -}}
{{- $list = append $list . -}}
{{- end -}}
{{- end -}}
{{- toJson $list -}}
{{- end -}}

{{- define "blocwerk.worker.podSpec" -}}
{{- $root := .root -}}
{{ include "blocwerk.podOptions" $root }}
securityContext:
  runAsNonRoot: true
  runAsUser: 10001   # "app" in both images
  runAsGroup: 999
  fsGroup: 999
  seccompProfile:
    type: RuntimeDefault
containers:
  - name: {{ .component }}
    image: {{ include "blocwerk.image" (list .v.image "") }}
    ports:
      - {name: http, containerPort: {{ .port }}}
    env:
      - name: COMPUTE_API_KEY
        valueFrom:
          secretKeyRef: {name: {{ $root.Values.secrets.compute }}, key: {{ .apiKey }}}
      # Private-network callbacks only to the app's service name (the app polls; no callback secret is set).
      - name: CALLBACK_ALLOWED_HOSTS
        value: {{ include "blocwerk.fullname" $root | quote }}
      {{- with include "blocwerk.workerCaptureEnv" $root }}{{ nindent 6 . }}{{ end }}
      {{- with include "blocwerk.envMap" .fixedEnv }}{{ nindent 6 . }}{{ end }}
      {{- with include "blocwerk.envMap" .v.env }}{{ nindent 6 . }}{{ end }}
    startupProbe:
      httpGet: {path: /health, port: http}
      periodSeconds: 5
      failureThreshold: 24
    livenessProbe:
      httpGet: {path: /health, port: http}
      periodSeconds: 30
      timeoutSeconds: 5
      failureThreshold: 3
    readinessProbe:
      httpGet: {path: /health, port: http}
      periodSeconds: 10
      timeoutSeconds: 5
    resources:
      {{- toYaml .v.resources | nindent 6 }}
    securityContext:
      {{- include "blocwerk.restrictedContainer" false | nindent 6 }}
    volumeMounts:
      - {name: tmp, mountPath: /tmp}
volumes:
  - name: tmp
    emptyDir:
      sizeLimit: {{ .v.tmpSizeLimit }}
{{- end -}}

{{- define "blocwerk.worker" -}}
{{- $name := printf "%s-%s" (include "blocwerk.fullname" .root) .component -}}
{{- $ctx := dict "root" .root "component" .component -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ $name }}
  labels:
    {{- include "blocwerk.labels" $ctx | nindent 4 }}
  annotations:
    # After the web app (wave 0) and the compute gate (wave 1), as autodeploy.sh ran its compute pass after the app.
    argocd.argoproj.io/sync-wave: "2"
spec:
  replicas: {{ .v.replicas }}
  strategy:
    type: Recreate
  selector:
    matchLabels:
      {{- include "blocwerk.selectorLabels" $ctx | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "blocwerk.selectorLabels" $ctx | nindent 8 }}
      annotations:
        blocwerk.app/rollout: {{ include "blocwerk.fingerprint" (include "blocwerk.worker.podSpec" .) | quote }}
    spec:
      {{- include "blocwerk.worker.podSpec" . | nindent 6 }}
---
apiVersion: v1
kind: Service
metadata:
  name: {{ $name }}
  labels:
    {{- include "blocwerk.labels" $ctx | nindent 4 }}
spec:
  selector:
    {{- include "blocwerk.selectorLabels" $ctx | nindent 4 }}
  ports:
    - {name: http, port: {{ .port }}, targetPort: http}
{{- end -}}
