<!-- One task log per completed task. Template: .claude/templates/task-log-template.md -->

# TASK-917: Документація зображень товарів (ADR-027)

**Date:** 2026-11-21
**Agent:** documentation-writer
**Status:** done (коміт не виконано, див. нижче)
**Duration:** —

## What was done
- ADR-027: статус «accepted, реалізовано в гілці», новий §12 зі статусом задач TASK-910…916, 12 фактичних відхилень від §2 (коди 413/415, семафори `MaxConcurrentUploads` і `MaxConcurrentProcessing`, таймаут 20 с → 422, пул 64 МБ, бюджет пам'яті при старті, `PublicPermitPerMinute`, grace страй-варіантів 7 днів і запобіжники, `CancellationToken.None` після commit, mobile `images[]`), відкриті залишки.
- Контракт §33.22: статус ADR, нові рядки конфігурації, §33.22.7 оновлено (7 днів, запобіжники), новий §33.22.11 з відхиленнями.
- Індекс ADR створено: `.claude/docs/adr/README.md` (ADR-019 і ADR-024 у каталозі відсутні).
- Runbook: новий §2f (міграція, GRANT, том, nginx, пам'ять, бекап, перехід `off`→`dry-run`→`delete`, ручне прибирання файлів видаленого tenant-а, dev без `Media__*`, відкат); рядки `Media__*` у §3.
- database.md: розділ `beauty_product_images` (розділ 8).
- known-issues: активні KI-027 (флейки SpecialistReviewsMigrationTests, ProductOrdersApiTests.client_abort), KI-028 (5 mobile-тестів P2 idempotency, падали до змін), KI-029 (database.md без таблиць ADR-025/026); resolved KI-030 (M2, очищувач) і KI-031 (M1, OOM upload) з root cause і prevention.

## Files changed
- `.claude/docs/adr/ADR-027-product-images-and-file-storage.md` — статус, §12
- `.claude/docs/beauty-contracts.md` — §33.22 (статус, конфіг, §33.22.3, §33.22.7, §33.22.11)
- `.claude/docs/adr/README.md` — новий індекс
- `.claude/docs/runbook.md` — §2f, рядки §3
- `.claude/docs/database.md` — `beauty_product_images`
- `.claude/docs/known-issues.md` — KI-027…031
- `.ai/task-logs/TASK-917-product-images-docs.md` — цей лог

## Decisions made
- Відхилення від ADR записані в ADR-027 §12 (уточнення), не як новий ADR. Вирішальний пункт: grace страй-варіантів 7 днів замість 24 год. Якщо команда вважає це зміною семантики, потрібен окремий ADR.
- Код не змінювався.

## Artifacts
- ADR-027 — updated; §33.22 — updated

## Tests
- Written: no
- Build / typecheck: not run (документація)
- Suite before/after: не змінювалось

## Model routing decision (spec §46)
```yaml
agent: documentation-writer
model:
  tier: cheap
  runtime: claude
  resolved_model: claude-haiku-5-5
  reason: документація з перевіркою коду
  escalations: []
```

## Metrics (spec §47)
```yaml
retry_count: 0
human_corrections: 0
review_findings: 0
tests_failed_before: 0
tests_failed_after: 0
context_compaction_count: 0
```

## Notes for next agent
- Коміт НЕ виконано: у сесії немає Bash-інструменту (git недоступний). Потрібно закомітити в поточній гілці: усі файли з «Files changed» плюс `docs/reviews/security-review-product-images.md` (зараз untracked).
- Не перевірено в цій сесії: прогін тестів, QA-таблиця TASK-914, назви 5 mobile-тестів (KI-028).
- Відкрито для людини: G3–G7 (runbook §2f), Q1 basic auth, m10 (digest `alpine:3`, шифрування tar).
