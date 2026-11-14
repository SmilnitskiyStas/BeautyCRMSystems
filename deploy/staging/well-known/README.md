# Universal Links / App Links (шаблони)

Файли в цьому каталозі: **заглушки**. Не публікувати, доки плейсхолдери не замінено реальними значеннями.

| Файл | Що замінити | Де взяти |
|---|---|---|
| `apple-app-site-association` | `__APPLE_TEAM_ID__`, `__IOS_BUNDLE_ID__` | Apple Developer, `mobile/app.config.ts` |
| `assetlinks.json` | `__ANDROID_PACKAGE__`, `__ANDROID_SHA256_CERT_FINGERPRINT__` (формат `AA:BB:...`) | Play Console / keystore, `mobile/app.config.ts` |

Домен для universal links на staging **ще не визначено** (відкрите рішення людини): шляхи мають віддаватися з того ж хоста,
що вказаний у мобільній збірці (`EXPO_PUBLIC_WEB_DOMAIN`). Публічні посилання закладів мають вигляд `/l/{publicCode}`.

Публікація (на сервері, людина): покласти заповнені файли в `/var/www/beauty-wellknown/` і додати в server-блок nginx

```nginx
location = /.well-known/apple-app-site-association {
    alias /var/www/beauty-wellknown/apple-app-site-association;
    default_type application/json;
    add_header Cache-Control "public, max-age=300" always;
}
location = /.well-known/assetlinks.json {
    alias /var/www/beauty-wellknown/assetlinks.json;
    default_type application/json;
    add_header Cache-Control "public, max-age=300" always;
}
```

Без редиректів, `Content-Type: application/json`, без basic auth на цих двох шляхах (`auth_basic off;`), інакше Apple/Google
не зможуть їх прочитати. Перевірка: `curl -sI https://beauty.<DOMAIN>/.well-known/assetlinks.json`.

Альтернатива статичним файлам: web сам віддає ці два шляхи з env (`APPLE_TEAM_ID`, `IOS_BUNDLE_ID`, `ANDROID_PACKAGE`,
`ANDROID_SHA256_CERT_FINGERPRINTS`; `frontend/lib/applinks/wellknown.ts`), без env відповідь 404. Тоді nginx проксіює ці шляхи
на web (приклад у `deploy/staging/README.md` §6.3), а файли в цьому каталозі не потрібні. Обирайте один варіант.
