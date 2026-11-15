import Constants from "expo-constants";

/** Значення з app.config.ts -> `extra` (плейсхолдери замінюються env на збірці). Домен у коді не зашивається. */
const extra = (Constants.expoConfig?.extra ?? {}) as { apiBaseUrl?: string; webDomain?: string };

export const config = {
  apiBaseUrl: extra.apiBaseUrl ?? "",
  webDomain: extra.webDomain ?? "",
  scheme: "beautycrm",
  appVersion: Constants.expoConfig?.version ?? "0.0.0",
};

export const linkConfig = { webDomain: config.webDomain, scheme: config.scheme };
