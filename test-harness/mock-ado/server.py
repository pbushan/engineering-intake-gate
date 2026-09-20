import base64
import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

EXPECTED_PAT = os.environ.get("MOCK_ADO_PAT", "synthetic-mock-ado-pat")
REQUESTS = []
COUNTS = {}
STATE = {}
CONTROL = {"failComment": False, "failTag": False, "forceConflict": False}

NORMAL_QUERY = "11111111-1111-1111-1111-111111111111"
EMPTY_QUERY = "00000000-0000-0000-0000-000000000000"
PAGED_QUERY = "22222222-2222-2222-2222-222222222222"
TRANSIENT_QUERY = "33333333-3333-3333-3333-333333333333"
PERMANENT_QUERY = "44444444-4444-4444-4444-444444444444"

PNG = base64.b64decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")
PDF = b"%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n2 0 obj<</Type/Pages/Count 0/Kids[]>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF\n"
TEXT_ATTACHMENT = "10700000-0000-4000-8000-000000000001"
IMAGE_ATTACHMENT = "10800000-0000-4000-8000-000000000001"
PDF_ATTACHMENT = "10900000-0000-4000-8000-000000000001"
FAILED_ATTACHMENT = "11000000-0000-4000-8000-000000000001"
ATTACHMENTS = {
    TEXT_ATTACHMENT: (b"Text attachment evidence password=SYNTH_MOCK_ATTACHMENT_SECRET_807", "text/plain"),
    IMAGE_ATTACHMENT: (PNG, "image/png"),
    PDF_ATTACHMENT: (PDF, "application/pdf"),
}


def fields(item_id, risk=None, custom=None):
    values = {
        "System.WorkItemType": "Generic Request",
        "System.Title": f"Synthetic work item {item_id}",
        "System.Description": "<p>Useful generic investigation context.</p>",
        "System.Tags": "generic; intake",
        "System.ChangedDate": "2026-09-10T12:00:00Z",
        "Custom.Boolean": True,
        "Custom.Number": 12.5,
    }
    if risk is not None:
        values["Example.State"] = risk
    if custom is not None:
        values["Custom.Structured"] = custom
    return values


def initial_comments(item_id):
    human = {"id": 1, "text": "Human-provided diagnostic context.", "createdDate": "2026-09-01T12:00:00Z", "createdBy": {"displayName": "Synthetic Person"}}
    validator = {"id": 2, "text": "Prior validator output <!-- engineering-intake-gate:validatorVersion=1;evaluationId=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee -->", "createdDate": "2026-09-01T12:01:00Z", "createdBy": {"displayName": "Synthetic Validator"}}
    if item_id == 104:
        return [human]
    if item_id in (105, 111):
        return [human, validator]
    return []


def state(item_id):
    if item_id not in STATE:
        risk = "Excluded" if item_id == 103 else None
        custom = {"nested": [1, True, None]} if item_id == 106 else None
        relations = [{
            "rel": "System.LinkTypes.Related",
            "url": "http://mock-ado:8081/generic-org/GenericProject/_apis/wit/workItems/900",
            "attributes": {"name": "Related"},
        }]
        if item_id in (107, 108, 109, 110):
            attachment_id = {107: TEXT_ATTACHMENT, 108: IMAGE_ATTACHMENT, 109: PDF_ATTACHMENT, 110: FAILED_ATTACHMENT}[item_id]
            name = {107: "evidence.txt", 108: "screen.png", 109: "document.pdf", 110: "unavailable.txt"}[item_id]
            size = len(ATTACHMENTS.get(attachment_id, (b"unavailable", ""))[0])
            relations.append({"rel": "AttachedFile", "url": f"http://mock-ado:8081/generic-org/_apis/wit/attachments/{attachment_id}", "attributes": {"name": name, "resourceSize": size}})
        values = fields(item_id, risk, custom)
        if item_id == 102:
            values["System.Description"] = ""
        if item_id == 101:
            values["Custom.SecretBearing"] = "api_key=sk-test_SYNTH_MOCK_FIELD_SECRET_801"
        STATE[item_id] = {"rev": item_id % 20 + 5, "fields": values, "relations": relations, "comments": initial_comments(item_id)}
    return STATE[item_id]


def item(item_id):
    current = state(item_id)
    return {"id": item_id, "rev": current["rev"], "fields": current["fields"], "relations": current["relations"]}


def legacy_item(item_id):
    risk = "Excluded" if item_id == 103 else None
    custom = {"nested": [1, True, None]} if item_id == 106 else None
    relations = [{
        "rel": "System.LinkTypes.Related",
        "url": "http://mock-ado:8081/generic-org/GenericProject/_apis/wit/workItems/900",
        "attributes": {"name": "Related"},
    }]
    if item_id in (107, 108, 109, 110):
        attachment_id = {107: TEXT_ATTACHMENT, 108: IMAGE_ATTACHMENT, 109: PDF_ATTACHMENT, 110: FAILED_ATTACHMENT}[item_id]
        name = {107: "evidence.txt", 108: "screen.png", 109: "document.pdf", 110: "unavailable.txt"}[item_id]
        size = len(ATTACHMENTS.get(attachment_id, (b"unavailable", ""))[0])
        relations.append({
            "rel": "AttachedFile",
            "url": f"http://mock-ado:8081/generic-org/_apis/wit/attachments/{attachment_id}",
            "attributes": {"name": name, "resourceSize": size},
        })
    values = fields(item_id, risk, custom)
    if item_id == 102:
        values["System.Description"] = ""
    if item_id == 101:
        values["Custom.SecretBearing"] = "api_key=sk-test_SYNTH_MOCK_FIELD_SECRET_801"
    return {"id": item_id, "rev": item_id % 20 + 5, "fields": values, "relations": relations}


def comments(item_id, page):
    values = state(item_id)["comments"]
    if item_id == 111 and not page:
        return values[:1], "comments-page-2"
    if item_id == 111:
        return values[1:], None
    return values, None


class Handler(BaseHTTPRequestHandler):
    server_version = "SyntheticAdo/1"

    def log_message(self, format, *args):
        return

    def _record(self):
        parsed = urlparse(self.path)
        auth = self.headers.get("Authorization", "")
        scheme = auth.split(" ", 1)[0] if auth else None
        mutation = self.command in ("PATCH", "PUT", "DELETE") or (
            self.command == "POST" and ("/workitems" in parsed.path.lower() or "/comments" in parsed.path.lower())
        )
        REQUESTS.append({
            "method": self.command,
            "path": parsed.path,
            "authorizationScheme": scheme,
            "authorizationPresent": bool(auth),
            "mutationEndpoint": mutation,
        })

    def _authorized(self):
        auth = self.headers.get("Authorization", "")
        expected = "Basic " + base64.b64encode((":" + EXPECTED_PAT).encode()).decode()
        return auth == expected

    def _json(self, status, value, headers=None):
        body = json.dumps(value, separators=(",", ":")).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        for key, header_value in (headers or {}).items():
            self.send_header(key, header_value)
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        parsed = urlparse(self.path)
        if parsed.path == "/health":
            return self._json(200, {"status": "ready"})
        if parsed.path == "/_mock/requests":
            return self._json(200, {"requests": REQUESTS})
        if parsed.path == "/_mock/state":
            return self._json(200, {"items": STATE})

        self._record()
        if not self._authorized():
            return self._json(401, {"message": "authentication failed"})

        lower = parsed.path.lower()
        query = parse_qs(parsed.query)
        if lower.endswith("/_apis/wit/workitemtypes"):
            return self._json(200, {"count": 1, "value": [{"name": "Generic Request"}]})
        if "/_apis/wit/wiql/" in lower:
            query_id = parsed.path.rsplit("/", 1)[-1].lower()
            if query_id == EMPTY_QUERY:
                return self._json(200, {"workItems": []})
            if query_id == PERMANENT_QUERY:
                return self._json(400, {"message": "invalid synthetic query"})
            if query_id == TRANSIENT_QUERY:
                count = COUNTS.get("transient-query", 0)
                COUNTS["transient-query"] = count + 1
                if count == 0:
                    return self._json(503, {"message": "synthetic transient"})
            if query_id == PAGED_QUERY and "continuationToken" not in query:
                return self._json(200, {"workItems": [{"id": 101}, {"id": 101}]}, {"x-ms-continuationtoken": "query-page-2"})
            if query_id == PAGED_QUERY:
                return self._json(200, {"workItems": [{"id": 102}]})
            return self._json(200, {"workItems": [{"id": value} for value in list(range(101, 113)) + [404]]})

        if "/_apis/wit/attachments/" in lower:
            attachment_id = parsed.path.rsplit("/", 1)[-1]
            if attachment_id == FAILED_ATTACHMENT:
                return self._json(503, {"message": "synthetic attachment failure"})
            if attachment_id not in ATTACHMENTS:
                return self._json(404, {"message": "missing attachment"})
            body, content_type = ATTACHMENTS[attachment_id]
            self.send_response(200)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            return self.wfile.write(body)

        if lower.endswith("/comments"):
            item_id = int(parsed.path.split("/")[-2])
            values, continuation = comments(item_id, query.get("continuationToken", [None])[0])
            headers = {"x-ms-continuationtoken": continuation} if continuation else None
            return self._json(200, {"comments": values}, headers)

        if "/_apis/wit/workitems/" in lower:
            item_id = int(parsed.path.rsplit("/", 1)[-1])
            if item_id == 404:
                return self._json(404, {"message": "missing synthetic item"})
            if item_id == 112:
                count = COUNTS.get("item-112", 0)
                COUNTS["item-112"] = count + 1
                if count == 0:
                    return self._json(503, {"message": "synthetic transient"})
            if item_id not in range(101, 113):
                return self._json(404, {"message": "outside synthetic fixture"})
            return self._json(200, item(item_id))

        return self._json(404, {"message": "unknown synthetic endpoint"})

    def _read_json(self):
        try:
            length = int(self.headers.get("Content-Length", "0"))
            return json.loads(self.rfile.read(length).decode())
        except (ValueError, UnicodeDecodeError, json.JSONDecodeError):
            return None

    def do_POST(self):
        self._record()
        parsed = urlparse(self.path)
        if parsed.path == "/_mock/reset":
            REQUESTS.clear()
            COUNTS.clear()
            STATE.clear()
            CONTROL.update({"failComment": False, "failTag": False, "forceConflict": False})
            return self._json(200, {"reset": True})
        if parsed.path == "/_mock/control":
            payload = self._read_json()
            if not isinstance(payload, dict) or any(key not in CONTROL or not isinstance(value, bool) for key, value in payload.items()):
                return self._json(400, {"message": "invalid synthetic control"})
            CONTROL.update(payload)
            return self._json(200, {"control": CONTROL})
        if not self._authorized():
            return self._json(401, {"message": "authentication failed"})
        lower = parsed.path.lower()
        if "/_apis/wit/workitems/" in lower and lower.endswith("/comments"):
            if CONTROL["failComment"] or os.environ.get("MOCK_ADO_FAIL_COMMENT") == "true":
                return self._json(503, {"message": "synthetic comment failure"})
            item_id = int(parsed.path.split("/")[-2])
            payload = self._read_json()
            if not isinstance(payload, dict) or not isinstance(payload.get("text"), str):
                return self._json(400, {"message": "invalid comment"})
            current = state(item_id)
            expected = self.headers.get("If-Match", "").strip('"')
            if expected and expected != str(current["rev"]):
                return self._json(412, {"message": "revision conflict"})
            comment_id = len(current["comments"]) + 1
            current["comments"].append({"id": comment_id, "text": payload["text"], "createdDate": "2026-09-10T12:00:00Z", "createdBy": {"displayName": "Synthetic Validator"}})
            current["rev"] += 1
            return self._json(200, {"id": comment_id})
        return self._json(405, {"message": "unsupported mutation"})

    def do_PATCH(self):
        self._record()
        parsed = urlparse(self.path)
        if not self._authorized():
            return self._json(401, {"message": "authentication failed"})
        if CONTROL["failTag"] or os.environ.get("MOCK_ADO_FAIL_TAG") == "true":
            return self._json(503, {"message": "synthetic tag failure"})
        if CONTROL["forceConflict"] or os.environ.get("MOCK_ADO_FORCE_CONFLICT") == "true":
            return self._json(412, {"message": "synthetic revision conflict"})
        lower = parsed.path.lower()
        if "/_apis/wit/workitems/" not in lower:
            return self._json(405, {"message": "unsupported mutation"})
        item_id = int(parsed.path.rsplit("/", 1)[-1])
        payload = self._read_json()
        if not isinstance(payload, list) or len(payload) != 2:
            return self._json(400, {"message": "invalid patch"})
        current = state(item_id)
        test, update = payload
        if test.get("op") != "test" or test.get("path") != "/rev" or str(test.get("value")) != str(current["rev"]):
            return self._json(412, {"message": "revision conflict"})
        if update.get("op") != "add" or update.get("path") != "/fields/System.Tags" or not isinstance(update.get("value"), str):
            return self._json(400, {"message": "invalid tag patch"})
        current["fields"]["System.Tags"] = update["value"]
        current["rev"] += 1
        return self._json(200, item(item_id))

    def do_PUT(self):
        self._record()
        return self._json(405, {"message": "writes are disabled"})

    def do_DELETE(self):
        self._record()
        return self._json(405, {"message": "writes are disabled"})


ThreadingHTTPServer(("0.0.0.0", 8081), Handler).serve_forever()
