/** Типи автентифікації. Відповідають `.claude/docs/beauty-contracts.md` §10 (UserDto, TokenResponse). */

export type Role = "owner" | "admin" | "deputy" | "specialist";

export interface SessionUser {
  id: string;
  email: string;
  fullName: string;
  role: Role;
  specialistId: string | null;
  /**
   * Права (§30.9.1, `GET /api/auth/me`): ефективні рівні за ВСІМА ключами каталогу, модулі вже враховані.
   * Відсутні, доки `me` не прочитано (або старий backend); тоді клієнт показує пресет ролі лише для відображення
   * (backend перевіряє незалежно, §30.5).
   */
  permissions?: Record<string, string>;
  /** Непрозорий штамп `{access_version}.{hash модулів}`; порівнюється з `X-Access-Version` як рядок. */
  accessStamp?: string;
  accessVersion?: number;
  catalogVersion?: number;
  /** Увімкнені модулі tenant (§30.9.1). */
  modules?: string[];
}

/** Відповідь BFF (`/api/session/login|refresh`). Refresh-токен сюди НЕ потрапляє — він лише в httpOnly-cookie. */
export interface SessionResponse {
  accessToken: string;
  expiresInSeconds: number;
  user: SessionUser;
}

export const ROLE_LABEL: Record<Role, string> = {
  owner: "Директор",
  admin: "Адміністратор",
  deputy: "Заступник",
  specialist: "Спеціаліст",
};

/** Демо-режим без backend: `NEXT_PUBLIC_USE_MOCK=1`. */
export const USE_MOCK = process.env.NEXT_PUBLIC_USE_MOCK === "1";

/** Базовий URL backend (без завершального `/`). Порожній = той самий origin. */
export const API_URL = (process.env.NEXT_PUBLIC_API_URL ?? "").replace(/\/+$/, "");
