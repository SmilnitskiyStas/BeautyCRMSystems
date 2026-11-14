import { useEffect, useRef, useState } from "react";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { humanizeBookingError } from "@beauty/booking-core";
import { catalogApi, localStore } from "@/services";
import { isNotFound } from "@/api/catalog";
import { normalizePublicCode } from "@/lib/deeplink";
import { toResolved, useChooseLocation } from "@/features/location/choose";
import { needsSwitchConfirmation, type ResolvedLocation } from "@/lib/locationSource";
import { Body, Button, ErrorText, H1, Screen, Spinner } from "@/ui";

/**
 * Deep link `beautycrm://l/{code}` і `https://{domain}/l/{code}` (expo-router мапить обидва на цей маршрут).
 * Параметр - недовірений: нормалізація+перевірка формату до запиту; успіх -> нова збережена локація (ADR-021 §2.1).
 */
export default function DeepLinkLocation() {
  const { t } = useTranslation();
  const router = useRouter();
  const { code: raw } = useLocalSearchParams<{ code?: string }>();
  const choose = useChooseLocation();
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState<ResolvedLocation | null>(null);
  const ran = useRef(false);

  useEffect(() => {
    if (ran.current) return;
    ran.current = true;
    const code = normalizePublicCode(Array.isArray(raw) ? raw[0] : raw);
    if (!code) return setError(t("location.codeInvalid"));
    (async () => {
      const resolved = toResolved(await catalogApi.resolve(code));
      const saved = await localStore.getLastLocation();
      // L7: інший заклад, ніж збережений, - не перемикаємо мовчки
      if (needsSwitchConfirmation(saved?.publicCode, resolved.publicCode)) return setPending(resolved);
      await choose(resolved, "replace");
    })()
      .catch((e) => setError(isNotFound(e) ? t("location.notFound") : humanizeBookingError(e)));
  }, [raw, choose, t]);

  return (
    <Screen>
      {pending ? (
        <>
          <H1>{t("location.switchTitle", { name: pending.name })}</H1>
          <Body muted>{t("location.switchBody")}</Body>
          <Button title={t("location.switchYes")} onPress={() => void choose(pending, "replace")} />
          <Button title={t("location.switchNo")} variant="secondary" onPress={() => router.replace("/")} />
        </>
      ) : error ? (
        <>
          <ErrorText>{error}</ErrorText>
          <Button title={t("location.title")} onPress={() => router.replace("/?change=1")} />
        </>
      ) : (
        <>
          <Body muted>{t("location.resolving")}</Body>
          <Spinner />
        </>
      )}
    </Screen>
  );
}
