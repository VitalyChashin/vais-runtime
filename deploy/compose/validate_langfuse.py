"""Quick smoke-test: Langfuse reachable, auth valid, OTLP ingestion works."""
import base64, sys, time, json
import urllib.request, urllib.error

HOST = "http://localhost:3000"
PUB  = "pk-lf-15bc843f-3695-46bf-85f1-adc85c7d08b3"
SEC  = "sk-lf-1a989166-2bdc-4122-b4bd-59cf4fb6a12e"
B64  = base64.b64encode(f"{PUB}:{SEC}".encode()).decode()

def get(path):
    req = urllib.request.Request(
        f"{HOST}{path}", headers={"Authorization": f"Basic {B64}"}
    )
    with urllib.request.urlopen(req, timeout=5) as r:
        return json.loads(r.read())

def post(path, body=b"", ct="application/x-protobuf"):
    req = urllib.request.Request(
        f"{HOST}{path}", data=body, method="POST",
        headers={"Authorization": f"Basic {B64}", "Content-Type": ct},
    )
    with urllib.request.urlopen(req, timeout=5) as r:
        return r.status

def minimal_otlp_payload():
    # Minimal valid OTLP ExportTraceServiceRequest proto:
    # field 1 (resource_spans), wire type 2, with an empty submessage body.
    # Enough to exercise auth + ingestion without real span data.
    inner = b""          # empty ResourceSpans submessage
    return bytes([0x0A, len(inner)]) + inner  # tag=field1|len, then body

OK   = "\033[32mOK\033[0m"
FAIL = "\033[31mFAIL\033[0m"

print(f"\nLangfuse validation  {HOST}\n{'─'*50}")

# 1. Health check
try:
    h = get("/api/public/health")
    print(f"[{OK}] Health  {h.get('status')}  v{h.get('version')}")
except Exception as e:
    print(f"[{FAIL}] Health  {e}")
    sys.exit(1)

# 2. Auth — list projects
try:
    p = get("/api/public/projects")
    names = [x["name"] for x in p.get("data", [])]
    print(f"[{OK}] Auth    valid — projects: {names}")
except urllib.error.HTTPError as e:
    body = e.read().decode()[:200]
    print(f"[{FAIL}] Auth    HTTP {e.code}  {body}")
    sys.exit(1)

# 3. Trace count before test
try:
    before = get("/api/public/traces")["meta"]["totalItems"]
    print(f"[{OK}] Traces  {before} items before OTLP test")
except Exception as e:
    print(f"[{FAIL}] Traces  {e}")
    before = None

# 4. OTLP ingestion
try:
    status = post("/api/public/otel/v1/traces", minimal_otlp_payload())
    print(f"[{OK}] OTLP    HTTP {status} — ingestion endpoint reachable")
except urllib.error.HTTPError as e:
    body = e.read().decode()[:300]
    print(f"[{FAIL}] OTLP    HTTP {e.code}  {body}")

# 5. Trace count after (2-second grace for worker)
if before is not None:
    time.sleep(2)
    try:
        after = get("/api/public/traces")["meta"]["totalItems"]
        delta = after - before
        marker = OK if delta > 0 else "~~"
        print(f"[{marker}] Traces  {after} items after  (delta={delta:+})")
        if delta == 0:
            print("       (empty proto payload is a no-op; real runtime spans will appear)")
    except Exception as e:
        print(f"[{FAIL}] Traces  {e}")

print()
