"""JSONL event log (logs/ is gitignored): decisions, orders, fills, pnl, breakers."""
import json
from datetime import datetime, timezone
from pathlib import Path


class JsonlLogger:
    def __init__(self, path, enabled=True, decisions=True):
        self.path, self.enabled, self.decisions = Path(path), enabled, decisions
        if enabled:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            self.f = self.path.open("a")

    def event(self, kind, **kw):
        if not self.enabled or (kind == "decision" and not self.decisions):
            return
        self.f.write(json.dumps({"logged_at": datetime.now(timezone.utc).isoformat(timespec="seconds"), "kind": kind, **kw}, default=str) + "\n")

    def close(self):
        if self.enabled: self.f.close()
