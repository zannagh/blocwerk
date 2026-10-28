{{/*
The web pod spec. Rendered once into the Deployment and once, hashed, into the deploy gate as its target fingerprint.
*/}}
{{- define "blocwerk.web.podSpec" -}}
{{- $fullname := include "blocwerk.fullname" . -}}
{{- $web := .Values.web -}}
{{ include "blocwerk.podOptions" . }}
terminationGracePeriodSeconds: 30
securityContext:
  {{- toYaml $web.podSecurityContext | nindent 2 }}
containers:
  - name: web
    image: {{ include "blocwerk.image" (list $web.image .Chart.AppVersion) }}
    imagePullPolicy: {{ $web.image.pullPolicy }}
    ports:
      - name: http
        containerPort: 5050   # ASPNETCORE_URLS in the image
    env:
      - name: POSTGRES__HOST
        value: {{ include "blocwerk.postgresHost" . | quote }}
      - name: POSTGRES__PORT
        value: {{ include "blocwerk.postgresPort" . | quote }}
      - name: POSTGRES__DATABASE
        value: {{ .Values.postgres.database | quote }}
      - name: POSTGRES__USERNAME
        value: {{ .Values.postgres.username | quote }}
      - name: POSTGRES__PASSWORD
        valueFrom:
          secretKeyRef: {name: {{ .Values.secrets.postgres }}, key: POSTGRES_PASSWORD}
      {{- if .Values.aspire.enabled }}
      - name: OTEL_EXPORTER_OTLP_ENDPOINT
        value: "http://{{ $fullname }}-aspire:18889"
      {{- end }}
      {{- if .Values.geometry.enabled }}
      - name: GEOMETRYSERVICE__URL
        value: "http://{{ $fullname }}-geometry:8000"
      - name: GEOMETRYSERVICE__APIKEY
        valueFrom:
          secretKeyRef: {name: {{ .Values.secrets.compute }}, key: COMPUTE_API_KEY}
      {{- end }}
      - name: SPLATSERVICE__ONRUNNERS
        value: {{ .Values.splat.onRunners | toString | quote }}
      {{- if .Values.splatCpu.enabled }}
      - name: SPLATSERVICE__URL
        value: "http://{{ $fullname }}-splat-cpu:8100"
      - name: SPLATSERVICE__APIKEY
        valueFrom:
          secretKeyRef: {name: {{ .Values.secrets.compute }}, key: SPLAT_API_KEY}
      {{- end }}
      {{- with include "blocwerk.envMap" (dict
            "CAPTURE__MAXVIDEOMB" .Values.capture.maxVideoMb
            "CAPTURE__MAXPHOTOMB" .Values.capture.maxPhotoMb
            "CAPTURE__MAXPHOTOS" .Values.capture.maxPhotos
            "CAPTURE__MAXVIDEOFRAMES" .Values.capture.maxVideoFrames) }}{{ nindent 6 . }}{{ end }}
      {{- with include "blocwerk.envMap" $web.env }}{{ nindent 6 . }}{{ end }}
    envFrom:
      - secretRef:
          name: {{ .Values.secrets.app }}
    startupProbe:
      httpGet: {path: /health, port: http}   # includes the database
      periodSeconds: 5
      failureThreshold: {{ $web.startupFailureThreshold }}
    # /alive, not /health: like the compose stack, a database blip must not take the app out of the Service, or
    # offline clients and the deploy gate lose the instance id they poll.
    livenessProbe:
      httpGet: {path: /alive, port: http}
      periodSeconds: 30
      timeoutSeconds: 5
      failureThreshold: 4
    readinessProbe:
      httpGet: {path: /alive, port: http}
      periodSeconds: 10
      timeoutSeconds: 5
      failureThreshold: 3
    resources:
      {{- toYaml $web.resources | nindent 6 }}
    securityContext:
      {{- toYaml $web.securityContext | nindent 6 }}
    volumeMounts:
      - {name: data, mountPath: /app/wall-images, subPath: wall-images}
      - {name: data, mountPath: /app/beta-videos, subPath: beta-videos}
      - {name: data, mountPath: /app/keys, subPath: keys}
      - {name: data, mountPath: /app/models, subPath: models, readOnly: true}
      {{- if .Values.secrets.appsettings }}
      - {name: appsettings, mountPath: /app/appsettings.json, subPath: appsettings.json, readOnly: true}
      {{- end }}
      - {name: tmp, mountPath: /tmp}
volumes:
  - name: data
    persistentVolumeClaim:
      claimName: {{ default (printf "%s-data" $fullname) $web.persistence.data.existingClaim }}
  {{- if .Values.secrets.appsettings }}
  - name: appsettings
    secret:
      secretName: {{ .Values.secrets.appsettings }}
  {{- end }}
  - name: tmp
    emptyDir:
      sizeLimit: {{ $web.persistence.tmpSizeLimit }}
{{- end -}}
