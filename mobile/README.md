# Beauty Booking (mobile)

Expo (SDK 58) + expo-router. Один універсальний застосунок, гостьова модель (ADR-021).

## Запуск

```bash
cd mobile
npm install            # окремо від root (mobile не входить у root workspaces)
EXPO_PUBLIC_API_URL=http://<LAN-IP>:5000 EXPO_PUBLIC_WEB_DOMAIN=app.beautycrm.example npx expo start
npm run typecheck
npm test
```

Проти staging (API і web на одному origin, без LAN):

```bash
EXPO_PUBLIC_API_URL=https://beauty.agrusystems.pp.ua EXPO_PUBLIC_WEB_DOMAIN=beauty.agrusystems.pp.ua npx expo start
```

Перевірено 2026-11-14 (HTTP, ті самі запити, що шле `@beauty/booking-core`): `/api/public/beauty/...` -> заклади, послуги, майстри, слоти, `POST /appointments` (201; повтор з тим самим `Idempotency-Key` -> 200 з тим самим токеном), читання й скасування за токеном. Код закладу для `/l/{code}` береться в адмінці (екран закладу).

Deep link у dev: `npx uri-scheme open "beautycrm://l/7K2M9QXA" --android|--ios`.
Universal/App Links працюють лише зі збіркою (EAS/dev client) і розгорнутими `apple-app-site-association` / `assetlinks.json` на `EXPO_PUBLIC_WEB_DOMAIN`.
Expo Go не підтримує власний домен universal links; `beautycrm://` працює.

## Конфіг

`app.config.ts` -> `extra`: `apiBaseUrl`, `webDomain` (плейсхолдери; значення через `EXPO_PUBLIC_*` у EAS profile).
