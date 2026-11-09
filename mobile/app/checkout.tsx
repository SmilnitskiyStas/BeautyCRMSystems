import { useEffect, useState } from "react";
import { Pressable, Text, View } from "react-native";
import { useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { humanizeBookingError, isSlotLost, type BookingResult, type CreateAppointmentRequest, type ReminderOption } from "@beauty/booking-core";
import { bookingSubmitter, localStore } from "@/services";
import { isRetryable } from "@/features/booking/submit";
import { REMINDER_OPTIONS, validateCheckoutForm } from "@/features/booking/checkoutForm";
import { BookingSummary } from "@/features/booking/BookingSummary";
import { pickOption } from "@/features/booking/summary";
import { Select } from "@/components/Select";
import { useDraft } from "@/state/draft";
import { cancellationLines, formatWhen } from "@/lib/format";
import { Body, Button, ErrorText, Field, H1, Label, Screen } from "@/ui";
import { colors, fonts, radius, space, TOUCH } from "@/theme";

/** Крок 4: ім'я/телефон + підтвердження. Оплата лише в закладі (cash, ADR-010 §6). */
export default function CheckoutScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { location, service, specialistName, specialist, slot } = useDraft();
  const reset = useDraft((d) => d.reset);

  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [email, setEmail] = useState("");
  const [reminder, setReminder] = useState<ReminderOption>("none");
  const [consent, setConsent] = useState(false); // ніколи не передзаповнено
  const [errors, setErrors] = useState<{ name?: string; phone?: string; email?: string; consent?: string }>({});
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
        setEmail((v) => v || p.email || "");
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
    const { errors: fieldErrors, client } = validateCheckoutForm({ name, phone, email, reminder });
    const next: typeof errors = { ...fieldErrors };
    if (!consent) next.consent = t("checkout.consentRequired");
    setErrors(next);
    if (!client || !consent) return;

    const req: CreateAppointmentRequest = {
      locationId: location.locationId,
      specialistId: slot.specialistId,
      serviceId: service.id,
      startsAt: slot.startsAt,
      client: { ...client, marketingConsent: false },
      reminder,
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
      await localStore.setProfile({ name: client.name, phone: client.phone, email: client.email });
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

  const terms = cancellationLines(slot.cancellation, t as never);

  return (
    <Screen>
      <BookingSummary location={location} service={service} specialist={specialist} startsAt={slot.startsAt} onChangeTime={() => router.back()} />
      {terms.length > 0 ? (
        <View style={{ gap: space.xs }} accessible accessibilityLabel={`${t("checkout.summaryPrice")}. ${terms.join(" ")}`}>
          <Label>{t("checkout.summaryPrice")}</Label>
          {terms.map((l, i) => (
            <Body key={i} muted>
              {l}
            </Body>
          ))}
        </View>
      ) : null}

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
      <View style={{ gap: space.xs }}>
        <Field
          label={t("checkout.email")}
          placeholder={t("checkout.emailPlaceholder")}
          value={email}
          onChangeText={setEmail}
          keyboardType="email-address"
          autoCapitalize="none"
          autoCorrect={false}
          autoComplete="email"
          textContentType="emailAddress"
          error={errors.email}
        />
        <Text style={{ fontFamily: fonts.body, fontSize: 13, color: colors.muted }}>{t("checkout.emailHint")}</Text>
      </View>

      <Select
        label={t("checkout.reminderTitle")}
        value={reminder}
        options={REMINDER_OPTIONS.map((o) => ({ value: o, label: t(`checkout.reminder.${o}`) }))}
        onChange={(v) => setReminder((cur) => pickOption(REMINDER_OPTIONS, cur, v))}
        closeLabel={t("checkout.close")}
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
