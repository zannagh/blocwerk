{{/*
The recognition worker (ghcr.io/zannagh/blocwerk-recognition): hold detection, outlines, markers, presence and
matching. Unlike geometry and splat-cpu it has no job API of its own: it pulls jobs from the web app over the runner
API as the instance runner (RECOGNITION__INSTANCEKEY, the same key the app reads), one at a time, and serves only
GET /health and /health/ready on 8080. A job it drops (pod replaced) goes back to the queue when its lease expires, so
no compute gate holds its rollout.
*/}}
{{- define "blocwerk.recognition.podSpec" -}}
{{- $fullname := include "blocwerk.fullname" . -}}
{{- $rec := .Values.recognition -}}
{{ include "blocwerk.podOptions" . }}
securityContext:
  runAsNonRoot: true
  runAsUser: 1654   # "app" in the .NET base image, as the web pod
  runAsGroup: 1654
  seccompProfile:
    type: RuntimeDefault
# The model files are on the web pod's data volume (ReadWriteOnce): run on the web pod's node.
affinity:
  podAffinity:
    requiredDuringSchedulingIgnoredDuringExecution:
      - labelSelector:
          matchLabels:
            {{- include "blocwerk.selectorLabels" (dict "root" . "component" "web") | nindent 12 }}
        topologyKey: kubernetes.io/hostname
containers:
  - name: recognition
    image: {{ include "blocwerk.image" (list $rec.image "") }}
    imagePullPolicy: {{ $rec.image.pullPolicy }}
    ports:
      - {name: http, containerPort: 8080}
    env:
      - name: RECOGNITION__SERVER
        value: "http://{{ $fullname }}:{{ .Values.web.service.port }}"
      - name: RECOGNITION__INSTANCEKEY
        valueFrom:
          secretKeyRef: {name: {{ .Values.secrets.recognition }}, key: RECOGNITION__INSTANCEKEY}
      {{- with include "blocwerk.envMap" $rec.env }}{{ nindent 6 . }}{{ end }}
    startupProbe:
      httpGet: {path: /health, port: http}
      periodSeconds: 5
      failureThreshold: 24
    livenessProbe:
      httpGet: {path: /health, port: http}   # 503 when the job loop is stuck
      periodSeconds: 30
      timeoutSeconds: 5
      failureThreshold: 3
    readinessProbe:
      httpGet: {path: /health/ready, port: http}
      periodSeconds: 10
      timeoutSeconds: 5
    resources:
      {{- toYaml $rec.resources | nindent 6 }}
    securityContext:
      {{- include "blocwerk.restrictedContainer" true | nindent 6 }}
    volumeMounts:
      # The web pod's models/ (climbingcrux.onnx, hold-segmenter-{encoder,decoder}.onnx); the image reads
      # RECOGNITION__MODELSDIR=/models. A model missing there is fetched from the app by hash into /tmp.
      - {name: data, mountPath: /models, subPath: models, readOnly: true}
      - {name: tmp, mountPath: /tmp}
volumes:
  - name: data
    persistentVolumeClaim:
      claimName: {{ default (printf "%s-data" $fullname) .Values.web.persistence.data.existingClaim }}
      readOnly: true
  - name: tmp
    emptyDir:
      sizeLimit: {{ $rec.tmpSizeLimit }}
{{- end -}}
