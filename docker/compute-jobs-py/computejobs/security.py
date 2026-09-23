"""Bearer-key auth and callback signing (Blocwerk compute job protocol v1)."""
import hashlib
import hmac

from fastapi import HTTPException, Request

from .settings import settings


def require_api_key(request: Request):
    """FastAPI dependency: enforce `Authorization: Bearer <COMPUTE_API_KEY>` when the key is set."""
    key = settings.api_key
    if not key:
        return
    header = request.headers.get("authorization", "")
    scheme, _, token = header.partition(" ")
    ok = scheme.lower() == "bearer" and hmac.compare_digest(token.strip().encode(), key.encode())
    if not ok:
        # never echo or log the presented token
        raise HTTPException(status_code=401, detail="missing or invalid API key",
                            headers={"WWW-Authenticate": "Bearer"})


def sign(body: bytes, secret: str) -> str:
    """Value of the X-Blocwerk-Signature header for a raw callback body."""
    return "sha256=" + hmac.new(secret.encode(), body, hashlib.sha256).hexdigest()


def verify(body: bytes, secret: str, header: str) -> bool:
    """Receiver-side check (reference implementation for the app)."""
    return hmac.compare_digest(sign(body, secret), header or "")
