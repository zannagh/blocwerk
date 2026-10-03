"""SSRF guard for outbound callbacks (the client chooses `callbackUrl`, the worker POSTs to it).

- only http/https, no userinfo, host required;
- the host is resolved ONCE and every resolved address must be allowed; the connection then goes to
  that pinned address (Host header / TLS SNI + certificate check still use the name), so a DNS answer
  that changes between check and connect (rebinding) cannot redirect the POST;
- never allowed: link-local (169.254.0.0/16, fe80::/10, incl. the cloud metadata endpoints),
  fd00:ec2::254, 100.100.100.200, 168.63.129.16, unspecified, multicast, reserved, broadcast;
- loopback / private / CGNAT / unique-local only with CALLBACK_ALLOW_PRIVATE=1, or for a host named in
  CALLBACK_ALLOWED_HOSTS (e.g. the app's compose service name `blocwerk`);
- redirects are never followed (http.client does not follow them; a 3xx counts as a failed attempt);
- short connect/read timeout.
"""
import http.client
import ipaddress
import socket
import ssl
from urllib.parse import urlsplit

from .settings import settings

ALWAYS_BLOCKED = [ipaddress.ip_network(n) for n in (
    "0.0.0.0/8", "169.254.0.0/16", "100.100.100.200/32", "168.63.129.16/32", "192.0.0.0/24",
    "224.0.0.0/4", "240.0.0.0/4", "::/128", "fe80::/10", "fd00:ec2::254/128", "ff00::/8")]
PRIVATE = [ipaddress.ip_network(n) for n in (
    "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "127.0.0.0/8", "::1/128",
    "fc00::/7", "198.18.0.0/15")]


class UnsafeTarget(ValueError):
    """The callback URL or what it resolves to is not allowed."""


def parse(url):
    """Syntax check -> (scheme, host, port, path+query). Raises UnsafeTarget."""
    if not isinstance(url, str) or len(url) > 2048 or any(c in url for c in "\r\n\t "):
        raise UnsafeTarget("callbackUrl must be an http(s) URL")
    try:
        u = urlsplit(url)
        port = u.port
    except ValueError as e:
        raise UnsafeTarget("callbackUrl is not a valid URL") from e
    if u.scheme not in ("http", "https") or not u.hostname:
        raise UnsafeTarget("callbackUrl must be an http(s) URL")
    if u.username is not None or u.password is not None:
        raise UnsafeTarget("callbackUrl must not contain credentials")
    port = port or (443 if u.scheme == "https" else 80)
    path = (u.path or "/") + (f"?{u.query}" if u.query else "")
    return u.scheme, u.hostname.lower().rstrip("."), port, path


def _allowed_hosts():
    return {h.strip().lower() for h in (settings.callback_allowed_hosts or "").split(",") if h.strip()}


def check_ip(ip, host):
    """Raise UnsafeTarget if the (resolved) address may not be called for `host`."""
    ip = ipaddress.ip_address(ip)
    if isinstance(ip, ipaddress.IPv6Address) and ip.ipv4_mapped is not None:
        ip = ip.ipv4_mapped
    if any(ip in n for n in ALWAYS_BLOCKED) or ip.is_link_local or ip.is_multicast or ip.is_unspecified:
        raise UnsafeTarget(f"callbackUrl host {host} resolves to a blocked address")
    private = any(ip in n for n in PRIVATE) or ip.is_private or ip.is_loopback or ip.is_reserved
    if private and not (settings.callback_allow_private or host in _allowed_hosts()):
        raise UnsafeTarget(f"callbackUrl host {host} is a private/loopback address "
                           "(set CALLBACK_ALLOW_PRIVATE=1 or CALLBACK_ALLOWED_HOSTS to allow it)")


def check_literal(url):
    """Cheap submission-time check (no DNS): syntax, and the host if it is an IP literal."""
    _, host, _, _ = parse(url)
    try:
        ipaddress.ip_address(host.strip("[]"))
    except ValueError:
        return url
    check_ip(host.strip("[]"), host)
    return url


def resolve(host, port):
    """Resolve once; every address must pass. Returns the first one (the one we connect to)."""
    try:
        infos = socket.getaddrinfo(host.strip("[]"), port, type=socket.SOCK_STREAM)
    except OSError as e:
        raise UnsafeTarget(f"callbackUrl host {host} does not resolve") from e
    addrs = [i[4][0] for i in infos]
    if not addrs:
        raise UnsafeTarget(f"callbackUrl host {host} does not resolve")
    for a in addrs:
        check_ip(a, host)
    return addrs[0]


class _PinnedHTTP(http.client.HTTPConnection):
    def __init__(self, host, port, ip, timeout):
        super().__init__(host, port, timeout=timeout)
        self._ip = ip

    def connect(self):
        self.sock = socket.create_connection((self._ip, self.port), self.timeout)


class _PinnedHTTPS(http.client.HTTPSConnection):
    def __init__(self, host, port, ip, timeout):
        super().__init__(host, port, timeout=timeout, context=ssl.create_default_context())
        self._ip = ip

    def connect(self):
        sock = socket.create_connection((self._ip, self.port), self.timeout)
        self.sock = self._context.wrap_socket(sock, server_hostname=self.host)


def post(url, body, headers, timeout):
    """POST to a vetted, pinned address. Returns the status code (redirects are not followed)."""
    scheme, host, port, path = parse(url)
    ip = resolve(host, port)
    cls = _PinnedHTTPS if scheme == "https" else _PinnedHTTP
    conn = cls(host, port, ip, timeout)
    try:
        conn.request("POST", path, body=body, headers=headers)
        resp = conn.getresponse()
        resp.read(65536)
        return resp.status
    finally:
        conn.close()
