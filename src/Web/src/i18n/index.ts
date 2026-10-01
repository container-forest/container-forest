import i18n, { type ParseKeys } from "i18next";
import LanguageDetector from "i18next-browser-languagedetector";
import { initReactI18next } from "react-i18next";
import en from "./locales/en";
import uk from "./locales/uk";

export const languages = ["uk", "en"] as const;

export type Language = (typeof languages)[number];

/** A key of the translations; zod schemas use these as messages, and the screen translates them when it renders. */
export type MessageKey = ParseKeys;

declare module "i18next" {
  interface CustomTypeOptions {
    defaultNS: "translation";
    resources: { translation: typeof uk };
  }
}

// The choice is kept per browser; without one, the browser's language decides, and anything else falls back to Ukrainian.
void i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources: { uk: { translation: uk }, en: { translation: en } },
    supportedLngs: languages,
    fallbackLng: "uk",
    load: "languageOnly",
    // Resources are bundled, so the first render already has them.
    initAsync: false,
    detection: {
      order: ["localStorage", "navigator"],
      lookupLocalStorage: "cf-language",
      caches: ["localStorage"],
    },
    // React escapes what it renders.
    interpolation: { escapeValue: false },
  });

document.documentElement.lang = i18n.resolvedLanguage ?? "uk";
i18n.on("languageChanged", (language) => {
  document.documentElement.lang = language;
});

export default i18n;
