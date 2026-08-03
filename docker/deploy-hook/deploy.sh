#!/bin/sh
# Runs once per accepted webhook delivery (HMAC already verified by the webhook receiver).
#
# The real work is detached with setsid so a slow `docker compose pull` can't blow past GitHub's
# ~10s delivery timeout — GitHub gets an immediate 200 while the deploy runs in the background.
# Output is redirected to PID 1's stdout so it shows up in `docker logs` for the receiver container.
setsid /usr/local/bin/run-deploy.sh >> /proc/1/fd/1 2>&1 &
echo "deploy queued"
