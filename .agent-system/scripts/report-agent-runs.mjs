#!/usr/bin/env node
// Agent run analytics from .ai/logs/agent-runs.jsonl. The implementation lives in
// hooks/scripts/lib/report-agent-runs.mjs (self-contained, so it also ships inside plugins
// and consuming projects for the /agent-stats command). This file is the repo entry point.
//
//   node scripts/report-agent-runs.mjs [--file <path>] [--since 7d|24h|2026-10-01] [--agent <id>] [--json]
import "../hooks/scripts/lib/report-agent-runs.mjs";
