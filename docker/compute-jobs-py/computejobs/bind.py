"""Refuse to listen beyond localhost without an API key (unless explicitly overridden)."""
import ipaddress
import os

LOOPBACK_NAMES = {"localhost", "localhost.localdomain"}


def is_loopback(host):
    if host in LOOPBACK_NAMES:
        return True
    try:
        return ipaddress.ip_address(host.strip("[]")).is_loopback
    except ValueError:
        return False


def check_bind(host, api_key, allow_open=None):
    """Return None if binding `host` is acceptable, else the reason it is refused.

    Beyond loopback, COMPUTE_API_KEY is mandatory unless ALLOW_OPEN_BIND=1 (e.g. a container whose
    port is only reachable on a private compose network)."""
    if allow_open is None:
        allow_open = os.environ.get("ALLOW_OPEN_BIND", "") in ("1", "true", "yes")
    if api_key or is_loopback(host) or allow_open:
        return None
    return (f"refusing to listen on {host} without COMPUTE_API_KEY: anyone who can reach this port "
            "could run jobs. Set COMPUTE_API_KEY (and put TLS in front), bind 127.0.0.1, or set "
            "ALLOW_OPEN_BIND=1 if the port is only reachable on a private network.")
