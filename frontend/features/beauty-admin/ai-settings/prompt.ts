/**
 * Нормалізація додатка до промпту (§20.6): клієнт дзеркалить сервер лише для лічильника.
 * NFC -> CRLF/CR -> LF -> без керівних символів (крім \n і \t) та zero-width/bidi -> не більше 2 порожніх рядків поспіль -> trim.
 */
const CONTROL = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]/g;
const INVISIBLE = /[​-‏‪-‮⁠-⁤﻿]/g;

export function normalizePrompt(text: string): string {
  return text
    .normalize("NFC")
    .replace(/\r\n?/g, "\n")
    .replace(CONTROL, "")
    .replace(INVISIBLE, "")
    .replace(/\n{4,}/g, "\n\n\n")
    .trim();
}

/** Довжина в кодових точках (емодзі = 1), як на сервері. */
export const codePointLength = (s: string): number => [...s].length;

export const promptLength = (text: string): number => codePointLength(normalizePrompt(text));

/**
 * Значення для `promptAddendum` у PUT: порожнє або рівне типовому -> `null` (= типовий текст), інакше нормалізований текст.
 */
export function promptForSave(text: string, defaultText: string): string | null {
  const n = normalizePrompt(text);
  return n === "" || n === normalizePrompt(defaultText) ? null : n;
}
