"""ADR-018 / beauty-contracts §27.2: generator of the ControlPlaneHmac v1 test vector.

Run from the repo root (stdlib only, isolated mode):
    python -I .ai/artifacts/contracts/ADR-018-hmac-test-vector.py

Writes ADR-018-hmac-test-vector.json next to this file (UTF-8, LF). The JSON is the
artifact consumed by tests in Beauty (TASK-817) and ShelfGuard (TASK-827, TASK-829).
Re-running must produce a byte-identical file: all inputs below are fixed.

The secret is a TEST-ONLY value. It must never be used in any environment.
"""
import base64
import hashlib
import hmac
import json
import pathlib

PREFIX = "BCRM-HMAC-SHA256-v1"
KID = "test-k1"
# 32 ASCII bytes; configured as base64 in ControlPlane:Inbound:Keys:{kid}:Secret.
SECRET_RAW = b"TEST-ONLY-NOT-A-REAL-SECRET-0001"
EMPTY_SHA256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

VECTORS = [
    {
        "name": "create_tenant_post",
        "method": "POST",
        "path": "/api/control/v1/tenants",
        "query": "",
        "timestamp": 1793700000,
        "nonce": "TEST_nonce_0000000000000001",
        "body": '{"externalRef":"sg-test-123","name":"Test Salon","slug":"test-salon",'
                '"modules":["beauty_booking"],"owner":{"email":"owner@example.test","fullName":"Test Owner"},'
                '"actor":{"id":"sg-admin-test","displayName":"Test Admin"}}',
    },
    {
        "name": "get_tenant_empty_body",
        "method": "GET",
        "path": "/api/control/v1/tenants/sg-test-123",
        "query": "",
        "timestamp": 1793700060,
        "nonce": "TEST_nonce_0000000000000002",
        "body": "",
    },
    {
        "name": "suspend_utf8_body",
        "method": "POST",
        "path": "/api/control/v1/tenants/sg-test-123/suspend",
        "query": "",
        "timestamp": 1793700120,
        "nonce": "TEST_nonce_0000000000000003",
        "body": '{"reason":"Несплата рахунку","actor":{"id":"sg-admin-test","displayName":"Тест Адмін"}}',
    },
]


def b64url_nopad(data: bytes) -> str:
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def canonical(v: dict, body: bytes) -> str:
    return "\n".join([
        PREFIX, KID, str(v["timestamp"]), v["nonce"], v["method"], v["path"], v["query"],
        hashlib.sha256(body).hexdigest(),
    ])


def sign(canon: str) -> str:
    return "v1=" + b64url_nopad(hmac.new(SECRET_RAW, canon.encode("utf-8"), hashlib.sha256).digest())


def main() -> None:
    assert len(SECRET_RAW) >= 32
    assert hashlib.sha256(b"").hexdigest() == EMPTY_SHA256
    out_vectors = []
    for v in VECTORS:
        body = v["body"].encode("utf-8")
        canon = canonical(v, body)
        signature = sign(canon)
        tampered = sign(canonical(v, body + b" "))
        assert tampered != signature
        out_vectors.append({
            "name": v["name"],
            "method": v["method"],
            "path": v["path"],
            "query": v["query"],
            "timestamp": v["timestamp"],
            "nonce": v["nonce"],
            "bodyUtf8": v["body"],
            "bodyBase64": base64.b64encode(body).decode("ascii"),
            "bodySha256Hex": hashlib.sha256(body).hexdigest(),
            "canonicalString": canon,
            "canonicalStringBase64": base64.b64encode(canon.encode("utf-8")).decode("ascii"),
            "headers": {
                "X-BCRM-Key-Id": KID,
                "X-BCRM-Timestamp": str(v["timestamp"]),
                "X-BCRM-Nonce": v["nonce"],
                "X-BCRM-Signature": signature,
            },
            "expectedSignature": signature,
            "negative": {
                "bodyWithTrailingSpaceSignature": tampered,
                "note": "signature of the same request with one extra trailing space in the body; a verifier holding expectedSignature must reject it",
            },
        })
    doc = {
        "artifact": "ADR-018-hmac-test-vector",
        "adr": ".claude/docs/adr/ADR-018-shelfguard-control-plane-integration.md",
        "contract": ".claude/docs/beauty-contracts.md#27.2",
        "version": "v1",
        "status": "complete",
        "generator": ".ai/artifacts/contracts/ADR-018-hmac-test-vector.py",
        "warning": "TEST-ONLY secret. Never configure it in any environment (dev, staging, prod).",
        "algorithm": {
            "mac": "HMAC-SHA256",
            "key": "raw bytes of base64-decoded ControlPlane:Inbound:Keys:{kid}:Secret (NOT the base64 text)",
            "canonical": "lines joined with LF, no trailing newline: prefix, kid, timestamp, nonce, METHOD, path, query, hex_lower(sha256(body bytes))",
            "canonicalEncoding": "UTF-8",
            "signatureHeader": "v1=<base64url without padding of the 32-byte MAC>",
            "keyFingerprint": "first 12 hex of sha256(raw key bytes); the only key-derived value allowed in DB/audit/logs",
        },
        "key": {
            "kid": KID,
            "secretRawAscii": SECRET_RAW.decode("ascii"),
            "secretBase64": base64.b64encode(SECRET_RAW).decode("ascii"),
            "fingerprint": hashlib.sha256(SECRET_RAW).hexdigest()[:12],
        },
        "emptyBodySha256Hex": EMPTY_SHA256,
        "verifierNotes": [
            "tests must freeze the clock at the vector timestamp (window |now - ts| <= 300 s)",
            "changing any of kid, timestamp, nonce, method, path, query or one body byte must change the signature",
            "non-empty query in v1 is rejected with 400 before signature verification",
        ],
        "vectors": out_vectors,
    }
    target = pathlib.Path(__file__).with_suffix(".json")
    target.write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    for v in out_vectors:
        print(v["name"], v["expectedSignature"])


if __name__ == "__main__":
    main()
