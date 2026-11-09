import { useEffect, useState } from "react";
import { Pressable, Text, View } from "react-native";
import { useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { clientSchema, humanizeBookingError, isSlotLost, type BookingResult, type CreateAppointmentRequest } from "@beauty/booking-core";
import { bookingSubmitter, localStore } from "@/services";
import { isRetryable } from "@/features/booking/submit";
import { useDraft } from "@/state/draft";
import { cancellationLines, formatPrice, formatWhen } from "@/lib/format";
import { Body, Button, ErrorText, Field, H1, Label, Screen } from "@/ui";
import { colors, fonts, radius, space, TOUCH } from "@/theme";

/** Крок 4: ім'я/телефон + підтвердження. Оплата лише в закладі (cash, ADR-010 §6). */
export default function CheckoutScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { location, service, specialistId, specialistName, slot } = useDraft();
  const reset = useDraft((d) => d.reset);

  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [consent, setConsent] = useState(false); // ніколи не передзаповнено
  const [errors, setErrors] = useState<{ name?: string; phone?: string; consent?: string }>({});
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<BookingResult | null>(null);

  useEffect(() => {
    if (!done && (!location || !service || !slot)) router.replace("/");
  }, [done, location, service, slot, router]);

  useEffect(() => {
    void localStore.getProfile().then((p) => {
      if (p) {
        setName((n) => n || p.name);
        setPhone((v) => v || p.phone);
      }
    });
  }, []);

  if (done) {
    return (
      <Screen>
        <H1>{t("checkout.doneTitle")}</H1>
        <Body>{done.appointment.serviceName}</Body>
        <Body>{formatWhen(done.appointment.startsAt)}</Body>
        <Body muted>{t("checkout.doneHint")}</Body>
        <Button
          title={t("checkout.toMyAppointments")}
          onPress={() => {
            reset();
            router.replace("/appointments");
          }}
        />
      </Screen>
    );
  }
  if (!location || !service || !slot) return null;

  const submit = async () => {
    const parsed = clientSchema.safeParse({ name, phone });
    const next: typeof errors = {};
    if (!parsed.success) {
      for (const i of parsed.error.issues) {
        const k = i.path[0] as "name" | "phone";
        next[k] ??= i.message;
      }
    }
    if (!consent) next.consent = t("checkout.consentRequired");
    setErrors(next);
    if (!parsed.success || !consent) return;

    const req: CreateAppointmentRequest = {
      locationId: location.locationId,
      specialistId: slot.specialistId,
      serviceId: service.id,
      startsAt: slot.startsAt,
      client: parsed.data,
      reminder: "none",
      paymentMethod: "cash",
    };
    setSubmitting(true);
    setError(null);
    try {
      const result = await bookingSubmitter.submit(location.tenantSlug, req, {
        locationName: location.name,
        serviceName: service.name,
        specialistName: specialistName || t("checkout.anySpecialist"),
      });
      await localStore.setProfile(parsed.data);
      setDone(result);
    } catch (e) {
      if (isSlotLost(e)) {
        useDraft.getState().setSlot(null);
        setError(t("checkout.slotLost"));
        router.back();
        return;
      }
      setError(isRetryable(e) ? `${humanizeBookingError(e)} ${t("checkout.retryHint")}` : humanizeBookingError(e));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <Screen>
      <View style={{ gap: space.xs }}>
        <Label>{t("checkout.summaryService")}</Label>
        <Body>{`${service.name} · ${service.durationMinutes} ${t("common.minutes")}`}</Body>
        <Label>{t("checkout.summarySpecialist")}</Label>
        <Body>{specialistId === "any" ? t("checkout.anySpecialist") : specialistName}</Body>
        <Label>{t("checkout.summaryWhen")}</Label>
        <Body>{`${location.name}, ${formatWhen(slot.startsAt)}`}</Body>
        <Label>{t("checkout.summaryPrice")}</Label>
        <Body>{formatPrice(service.priceFinal)}</Body>
        {cancellationLines(slot.cancellation, t as never).map((l) => (
          <Body key={l} muted>
            {l}
          </Body>
        ))}
      </View>

      <Field label={t("checkout.name")} value={name} onChangeText={setName} autoComplete="name" textContentType="name" error={errors.name} />
      <Field
        label={t("checkout.phone")}
        placeholder={t("checkout.phonePlaceholder")}
        value={phone}
        onChangeText={setPhone}
        keyboardType="phone-pad"
        autoComplete="tel"
        textContentType="telephoneNumber"
        error={errors.phone}
      />

      <Pressable
        accessibilityRole="checkbox"
        accessibilityState={{ checked: consent }}
        onPress={() => setConsent((c) => !c)}
        style={{ minHeight: TOUCH, flexDirection: "row", alignItems: "center", gap: space.md }}
      >
        <View style={{ width: 28, height: 28, borderRadius: 6, borderWidth: 2, borderColor: colors.primary, backgroundColor: consent ? colors.primary : colors.surface, alignItems: "center", justifyContent: "center" }}>
          {consent ? <Text style={{ color: colors.onPrimary, fontFamily: fonts.bodyMedium }}>✓</Text> : null}
        </View>
        <Text style={{ flex: 1, fontFamily: fonts.body, fontSize: 14, color: colors.text, borderRadius: radius.md }}>{t("checkout.consent")}</Text>
      </Pressable>
      {errors.consent ? <ErrorText>{errors.consent}</ErrorText> : null}

      <Body muted>{t("checkout.payment")}</Body>
      {error ? <ErrorText>{error}</ErrorText> : null}
      <Button title={submitting ? t("checkout.submitting") : t("checkout.confirm")} onPress={() => void submit()} loading={submitting} />
    </Screen>
  );
}
