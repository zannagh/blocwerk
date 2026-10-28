{{/* The pod template of a gate Job. Called with: root, script, env (a list of EnvVar). */}}
{{- define "blocwerk.gatePod" -}}
{{- $root := .root -}}
{{- $gate := $root.Values.deployGate -}}
metadata:
  labels:
    {{- include "blocwerk.selectorLabels" (dict "root" $root "component" "deploy-gate") | nindent 4 }}
spec:
  restartPolicy: Never
  serviceAccountName: {{ include "blocwerk.fullname" $root }}-deploy-gate
  automountServiceAccountToken: true   # the only pods that read the kube API (pods in this namespace)
  {{- with include "blocwerk.placement" $root | trim }}{{ nindent 2 . }}{{ end }}
  securityContext:
    runAsNonRoot: true
    runAsUser: 100   # curl_user in the image
    runAsGroup: 101
    seccompProfile:
      type: RuntimeDefault
  containers:
    - name: gate
      image: {{ include "blocwerk.image" (list $gate.image "") }}
      command: ["/bin/sh", "/gate/{{ .script }}"]
      env:
        - {name: RELEASE, value: {{ include "blocwerk.fullname" $root | quote }}}
        - {name: POLL_SECONDS, value: {{ $gate.pollSeconds | quote }}}
        {{- toYaml .env | nindent 8 }}
      resources:
        requests: {memory: 16Mi}
        limits: {memory: 64Mi}
      securityContext:
        {{- include "blocwerk.restrictedContainer" true | nindent 8 }}
      volumeMounts:
        - {name: gate, mountPath: /gate, readOnly: true}
  volumes:
    - name: gate
      configMap:
        name: {{ include "blocwerk.fullname" $root }}-deploy-gate
{{- end -}}
