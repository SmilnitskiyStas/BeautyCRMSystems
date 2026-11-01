<!-- Artifact header (spec §22-23). Validated by schemas/artifact.schema.json.
     Put this block at the top of any file under .ai/artifacts/<kind>/.
     The body format depends on `type` (OpenAPI/JSON for API_CONTRACT, SQL/prose for
     DATA_SCHEMA, markdown for UI_SPEC / RESEARCH_REPORT / REQUIREMENTS / TEST_PLAN, etc.). -->

```yaml
id: <UPPER-KEBAB-ID>          # e.g. API-CONTRACT-REFUND-V1
type: <API_CONTRACT | DATA_SCHEMA | UI_SPEC | RESEARCH_REPORT | REQUIREMENTS | ARCHITECTURE_DECISION | TEST_PLAN | SECURITY_FINDINGS | CONTEXT_PACKAGE>
version: 1
created_by: <agent-id>
task_id: TASK-XXX
status: draft                 # draft | review | accepted | superseded | deprecated
consumers: [<agent-id or TASK-XXX>]
source_files:
  - <repo-relative path this artifact is authoritative for>
# supersedes: <artifact id this version replaces>
```

---

<artifact body>
