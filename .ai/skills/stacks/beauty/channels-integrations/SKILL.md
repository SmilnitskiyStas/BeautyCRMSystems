---
name: channels-integrations
description: Адаптери каналів зв'язку (Telegram, Instagram, Messenger, WhatsApp, Viber, віджет) і outbox. Використовуй при інтеграціях каналів.
---

- Єдиний інтерфейс `IChannelAdapter`: прийом webhook, відправка, перевірка підпису.
- Перший реліз: Telegram (Bot API) і Instagram (Meta Graph API, спершу `MockInstagramAdapter`). Решту каналів лише за інтерфейсом.
- Вхідні повідомлення нормалізуються в `beauty_conversations` / `beauty_messages` і йдуть у чергу; вихідні через outbox з ретраями ×3.
- Підпис webhook обов'язковий; невалідний підпис відхиляється.
- Токени лише з `integration_configs`/`.env`, у відповідях маскувати (останні 4 символи).
- Враховувати обмеження Meta (вікно повідомлень, шаблони) і правила Telegram.
- Налаштування каналу: `inbox`, `booking`, `ai`, `mode`, `greeting`, правила передачі менеджеру.
- На час розробки `Mock*Adapter` для всіх каналів.
