import type { ExpoConfig } from "expo/config";

/**
 * Домен і API - ПЛЕЙСХОЛДЕРИ (ADR-010 §3). Реальні значення задаються env на етапі збірки (EAS profile):
 *   EXPO_PUBLIC_WEB_DOMAIN  - домен universal/app links (де лежать apple-app-site-association і assetlinks.json)
 *   EXPO_PUBLIC_API_URL     - origin backend (без кінцевого слеша)
 * У коді застосунку домен не зашивається: його читає src/config.ts з `extra`.
 */
const WEB_DOMAIN = process.env.EXPO_PUBLIC_WEB_DOMAIN ?? "app.beautycrm.example";
const API_URL = process.env.EXPO_PUBLIC_API_URL ?? "https://api.beautycrm.example";
const BUNDLE_ID = process.env.EXPO_PUBLIC_BUNDLE_ID ?? "com.beautycrm.booking";

const config: ExpoConfig = {
  name: "Beauty Booking",
  slug: "beauty-booking",
  version: "0.1.0",
  scheme: "beautycrm",
  orientation: "portrait",
  userInterfaceStyle: "light",
  backgroundColor: "#F6F4F8",
  ios: {
    bundleIdentifier: BUNDLE_ID,
    supportsTablet: false,
    associatedDomains: [`applinks:${WEB_DOMAIN}`],
  },
  android: {
    package: BUNDLE_ID,
    // L8: токени/профіль не потрапляють у хмарний бекап Android (Auto Backup / device transfer)
    allowBackup: false,
    intentFilters: [
      {
        action: "VIEW",
        autoVerify: true,
        data: [{ scheme: "https", host: WEB_DOMAIN, pathPrefix: "/l/" }],
        category: ["BROWSABLE", "DEFAULT"],
      },
    ],
  },
  plugins: [
    "expo-router",
    "expo-secure-store",
    "expo-localization",
    "expo-font",
    [
      "expo-location",
      {
        // Дозвіл запитується лише на кроці «заклади поблизу» (ADR-011 §4); координати не зберігаються.
        locationWhenInUsePermission:
          "Геолокація потрібна лише щоб один раз знайти найближчі заклади. Ми не зберігаємо ваші координати.",
      },
    ],
    [
      "expo-camera",
      { cameraPermission: "Камера потрібна лише щоб відсканувати QR-код закладу." },
    ],
  ],
  experiments: { typedRoutes: true },
  extra: {
    apiBaseUrl: API_URL,
    webDomain: WEB_DOMAIN,
  },
};

export default config;
