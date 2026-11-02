import { BeautyApiError } from "@/features/beauty-auth/errors";
import { CLIENT_SEED, PHONE_SEED } from "../../beauty-shared/demo-seed";
import { maskPhone } from "../phone";
import type { ClientPhoneInfo, PhonePendingAction, PhonePendingResult, PhonePendingSource, PhoneSource } from "../types";

/**
 * Mock §25.7: телефон клієнта, джерело, згода на дзвінок, pending-номер і м'який зв'язок «можливий дубль».
 * `POST /clients/{id}/phone-pending`: 404 client_not_found, 409 no_pending_phone, 422 invalid_action, specialist -> 403.
 * Повний номер віддають лише картка клієнта й `call-tasks`; для inbox є тільки маска (`maskedOf`).
 */

interface PhoneState {
  phone: string | null;
  source?: PhoneSource;
  consentAt?: number;
  pending?: { phone: string; source: PhonePendingSource; at: number };
  duplicateOf?: string;
}

interface Deps {
  role: () => string;
  now?: () => number;
  /** `false` - без затримки (тести). */
  wait?: boolean;
}

const err = (status: number, code: string) => new BeautyApiError(status, code, code);

export function createMockPhones(deps: Deps) {
  const now = deps.now ?? (() => Date.now());
  const t0 = now();
  const isManager = () => deps.role() === "owner" || deps.role() === "admin";
  const state = new Map<string, PhoneState>();
  for (const c of CLIENT_SEED) {
    const seed = PHONE_SEED.find((p) => p.clientId === c.id);
    state.set(c.id, {
      phone: c.phone,
      ...(seed?.source ? { source: seed.source } : {}),
      ...(seed?.consentDaysAgo !== undefined ? { consentAt: t0 - seed.consentDaysAgo * 86_400_000 } : {}),
      ...(seed?.pending ? { pending: { phone: seed.pending.phone, source: seed.pending.source, at: t0 - seed.pending.minutesAgo * 60_000 } } : {}),
      ...(seed?.duplicateOf ? { duplicateOf: seed.duplicateOf } : {}),
    });
  }
  const iso = (ms: number) => new Date(ms).toISOString();
  const wait = async <T>(v: T): Promise<T> => {
    if (deps.wait !== false) await new Promise((r) => setTimeout(r, 120));
    return structuredClone(v);
  };

  return {
    /** Поточний повний номер (для `call-tasks` і `phoneAvailable`). */
    phoneOf: (clientId: string): string | null => state.get(clientId)?.phone ?? null,
    pendingOf: (clientId: string): string | undefined => state.get(clientId)?.pending?.phone,
    /** Для inbox: лише маска. */
    maskedOf: (clientId: string): string | undefined => {
      const p = state.get(clientId)?.phone;
      return p ? maskPhone(p) : undefined;
    },
    info(clientId: string): ClientPhoneInfo | undefined {
      const s = state.get(clientId);
      if (!s) return undefined;
      const dup = s.duplicateOf ? CLIENT_SEED.find((c) => c.id === s.duplicateOf) : undefined;
      return {
        phone: s.phone,
        ...(s.source ? { phoneSource: s.source } : {}),
        ...(s.consentAt !== undefined ? { callConsentAt: iso(s.consentAt) } : {}),
        ...(s.pending ? { phonePending: { phone: s.pending.phone, source: s.pending.source, at: iso(s.pending.at) } } : {}),
        ...(dup ? { possibleDuplicate: { id: dup.id, name: dup.name } } : {}),
      };
    },
    resolvePhonePending: async (clientId: string, action: PhonePendingAction): Promise<PhonePendingResult> => {
      if (!isManager()) throw err(403, "forbidden_role");
      const s = state.get(clientId);
      if (!s) throw err(404, "client_not_found");
      if (action !== "accept" && action !== "reject") throw err(422, "invalid_action");
      if (!s.pending) throw err(409, "no_pending_phone");
      if (action === "accept") {
        s.phone = s.pending.phone;
        s.source = s.pending.source;
        s.consentAt = s.pending.at;
      }
      s.pending = undefined;
      return wait({
        phone: s.phone ?? "",
        ...(s.source ? { phoneSource: s.source } : {}),
        ...(s.consentAt !== undefined ? { callConsentAt: iso(s.consentAt) } : {}),
        phonePending: null,
      });
    },
  };
}

export type MockPhones = ReturnType<typeof createMockPhones>;
