/** Типи автентифікації. Відповідають `.claude/docs/beauty-contracts.md` §10 (UserDto, TokenResponse). */

export type Role = "owner" | "admin" | "specialist";

export interface SessionUser {
  id: string;
  email: string;
  fullName: string;
  role: Role;
  specialistId: string | null;
}

/** Відповідь BFF (`/api/session/login|refresh`). Refresh-токен сюди НЕ потрапляє — він лише в httpOnly-cookie. */
export interface SessionResponse {
  accessToken: string;
  expiresInSeconds: number;
  user: SessionUser;
}

export const ROLE_LABEL: Record<Role, string> = {
  owner: "Власник",
  admin: "Адміністратор",
  specialist: "Спеціаліст",
};

/** Демо-режим без backend: `NEXT_PUBLIC_USE_MOCK=1`. */
export const USE_MOCK = process.env.NEXT_PUBLIC_USE_MOCK === "1";

/** Базовий URL backend (без завершального `/`). Порожній = той самий origin. */
export const API_URL = (process.env.NEXT_PUBLIC_API_URL ?? "").replace(/\/+$/, "");
