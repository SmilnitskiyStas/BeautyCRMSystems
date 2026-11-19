import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import { getLocales } from "expo-localization";
import { uk } from "./uk";

/**
 * Українська - єдина мова Phase 1: `lng: "uk"` примусово, `expo-localization` лише визначає locale пристрою для форматів
 * (інші мови з'являться окремими ресурсами). Рядки поза React - `i18n.t`.
 */
export const deviceLocale = getLocales()[0]?.languageTag ?? "uk-UA";

void i18n.use(initReactI18next).init({
  resources: { uk: { translation: uk } },
  lng: "uk",
  fallbackLng: "uk",
  interpolation: { escapeValue: false },
});

export default i18n;
