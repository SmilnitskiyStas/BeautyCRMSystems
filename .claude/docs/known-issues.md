# Known Issues

**Owner:** qa-engineer (підтримує структуру файлу) — **записує будь-який агент**, що знайшов чи виправив проблему
**Updated:** 2026-11-16

Два різні розділи нижче — не плутати:
- **Active Issues** — те, що зараз зламано й ще не виправлено (звичайний баг-трекер).
- **Resolved Issues** — те, що вже виправлено, **з обов'язковими `Root cause` і `Prevention`**. Це журнал «не наступати на ті самі граблі», який читає кожен агент перед стартом задачі (retrieval за area).

## Format

```
### KI-NNN: [Title]
Severity: critical / high / medium / low
Status: open / accepted-risk / resolved
Discovered in: TASK-XXX
Resolved in: TASK-YYY (якщо resolved)
Area: frontend / backend / database / security / infra / ...

Description: [симптом — що спостерігалось, за яких умов відтворюється]
Root cause: [лише для resolved]
Resolution: [що конкретно змінено]
Prevention: [лише для resolved — конкретне, дієве правило]
```

## Active Issues

### KI-001: Access-токен 15 хв не покриває зміну пароля і «вийти з усіх пристроїв»
Severity: low
Status: open
Discovered in: TASK-857 (security-review, minor 13)
Area: security, backend

Description: після зміни пароля або виходу з усіх пристроїв уже видані access-токени діють до закінчення TTL (15 хв). Права й активність перевіряються з БД на кожен запит (ADR-023 §2.5), тож вимкнення користувача і зміна прав діють одразу (`401 session_revoked` / `403`). Недостатньо покрито саме сценарій пароля й logout-all: їхній відкат не інвалідує access-токен миттєво.

Resolution: планується — перевірка `sessions_valid_from`/версії сесії в `AccessContextMiddleware` (окрема задача, потребує рішення щодо TTL і навантаження на БД).

### KI-002: ChatFlowLlmTests — нестабільний тест (передує ADR-023)
Severity: low
Status: open
Discovered in: до TASK-848 (pre-existing; зафіксовано при TASK-858)
Area: backend, tests

Description: `backend/BeautyCrm.Tests/Conversations/ChatFlowLlmTests.cs` періодично падає. Не пов'язаний зі змінами прав ADR-023. Причину не розібрано.

Resolution: окремий root-cause аналіз (`root-cause-analysis`) і виправлення або ізоляція тесту. До того не вважати його сигналом регресії.

### KI-003: ChatLiveSmokeFixesTests — нестабільні тести
Severity: low
Status: open
Discovered in: до TASK-858
Area: backend, tests

Description: `backend/BeautyCrm.Tests/Conversations/ChatLiveSmokeFixesTests.cs` (PostgreSQL-тести потоку чату) нестабільні. Не пов'язані з ADR-023.

Resolution: окремий аналіз; до виправлення повторювати прогін перед висновком про регресію.

### KI-004: F3 — розширення scope `own` через не скасований запис (accept-risk)
Severity: medium
Status: accepted-risk
Discovered in: TASK-848/857 (security-review, F3)
Area: security, backend

Description: майстер з `clients.view = own` може створити запис для клієнта поза своїм scope, якщо збіг за телефоном знайдено; відповідь не розкриває збережені дані клієнта (M3), але створений запис, який не скасовано, розширює його scope на цього клієнта. Скасований запис, створений самим актором, scope не розширює.

Accept-risk: прийнято власником продукту. Контроль: структурований лог `client_scope_expanded {actorUserId, clientId, appointmentId}` і метрика `beauty.access.client_scope_expanded` (без ПІІ). Переглядати метрику при релізі; при зростанні — повернутися до питання.

### KI-005: Frontend-тести лише з `--maxWorkers=2`
Severity: low
Status: open
Discovered in: TASK-800 (ADR-016)
Area: frontend, infra

Description: при паралельному запуску vitest з jsdom тести нестабільні під навантаженням. CI запускає `npm test -- --maxWorkers=2`.

Resolution: лишати `--maxWorkers=2` у CI і в локальних командах; дослідити джерело нестабільності jsdom окремо.

### KI-006: Немає retention-job воркера для `purge_user_access_audit`
Severity: medium
Status: open
Discovered in: TASK-849 (ADR-023 Q7)
Area: backend, worker, database

Description: журнал `user_access_audit` має зберігатися 3 роки і очищуватися через `purge_user_access_audit(p_before)`. Функція створена і доступна лише `beautycrm_worker`, але воркер її не викликає. Журнал росте без обмеження.

Resolution: окрема задача воркера (планове очищення з `p_before = now() - 3 years`, метрика видалених рядків). Потребує окремого TASK.

### KI-007: Залишок `DEFAULT '{}'` на `invites.permissions`
Severity: low
Status: open
Discovered in: TASK-849 / TASK-858
Area: database

Description: контракт §2.4 передбачав прибрати `DEFAULT '{}'` після TASK-851. Міграція `20261115120000` його залишає, бо старі образи API створюють запрошення без поля `permissions`. Поточний код завжди передає повний знімок, тож default зайвий.

Resolution: окрема міграція `DROP DEFAULT` після виведення старих образів API з обігу. Не змінювати без human gate на staging/prod.

## Resolved Issues
_(порожньо)_
