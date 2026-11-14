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

Deep link у dev: `npx uri-scheme open "beautycrm://l/7K2M9QXA" --android|--ios`.
Universal/App Links працюють лише зі збіркою (EAS/dev client) і розгорнутими `apple-app-site-association` / `assetlinks.json` на `EXPO_PUBLIC_WEB_DOMAIN`.
Expo Go не підтримує власний домен universal links; `beautycrm://` працює.

## Конфіг

`app.config.ts` -> `extra`: `apiBaseUrl`, `webDomain` (плейсхолдери; значення через `EXPO_PUBLIC_*` у EAS profile).
