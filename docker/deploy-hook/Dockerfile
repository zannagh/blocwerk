# Tiny GitHub-webhook receiver that redeploys the blocwerk service when a new image is published.
#
# adnanh/webhook verifies GitHub's HMAC signature (X-Hub-Signature-256) and then runs the deploy
# scripts, which drive the HOST docker daemon over the mounted /var/run/docker.sock. Built on the
# official docker CLI image so `docker` + `docker compose` are available; Alpine-based to stay small.
FROM docker:27-cli

# webhook: the receiver. docker-cli-compose: the `docker compose` subcommand used by the deploy.
RUN apk add --no-cache webhook docker-cli-compose

COPY hooks.json /config/hooks.json
COPY deploy.sh run-deploy.sh /usr/local/bin/
RUN chmod +x /usr/local/bin/deploy.sh /usr/local/bin/run-deploy.sh

EXPOSE 9000

# -template renders hooks.json as a Go template first, so the secret comes from $WEBHOOK_SECRET
# at runtime instead of being baked into the image. -urlprefix ci + hook id "released" means the
# endpoint is /ci/released, matching the GitHub webhook's Payload URL (…/ci/released).
ENTRYPOINT ["webhook", "-template", "-urlprefix", "ci", "-hooks", "/config/hooks.json", "-ip", "0.0.0.0", "-port", "9000", "-verbose"]
