import { useEffect, useMemo, useState } from "react";
import { StyleSheet, Text, View } from "react-native";
import { useRouter } from "expo-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ANY_SPECIALIST_ID, humanizeBookingError } from "@beauty/booking-core";
import { longDate, todayIn } from "@/lib/dates";
import { firstAvailable, monthOf } from "@/lib/calendar";
import { bookingApi } from "@/services";
import { useDraft } from "@/state/draft";
import { Avatar } from "@/features/booking/Avatar";
import { MonthCalendar } from "@/features/booking/MonthCalendar";
import { Body, Button, Chip, ErrorText, H1, Label, Screen, Spinner } from "@/ui";
import { colors, fonts, radius, space } from "@/theme";

const HORIZON_DAYS = 62;

/** Крок 4: дата (місячний календар за доступністю) і час. Майстра обирають на попередньому кроці. */
export default function TimeScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { location, service, specialist, specialistId, date } = useDraft();
  const { setDate, setSlot } = useDraft.getState();

  useEffect(() => {
    if (!location || !service) router.replace("/");
  }, [location, service, router]);

  const tenant = location?.tenantSlug ?? "";
  const locationId = location?.locationId ?? "";
  const from = useMemo(() => (location ? todayIn(location.timezone) : ""), [location]);

  const avail = useQuery({
    queryKey: ["availability", tenant, locationId, service?.id ?? null, specialistId, from],
    queryFn: () =>
      bookingApi.getAvailability(tenant, {
        locationId,
        serviceId: service!.id,
        specialistId: specialistId === ANY_SPECIALIST_ID ? undefined : specialistId,
        from,
        days: HORIZON_DAYS,
      }),
    enabled: !!location && !!service && !!from,
  });
  const days = useMemo(() => avail.data ?? [], [avail.data]);

  // вибір дати: лише available. Якщо збережена дата вже не доступна - скидаємо на першу доступну (або порожньо)
  const dateOk = !!date && days.some((d) => d.date === date && d.status === "available");
  useEffect(() => {
    if (!avail.data) return;
    if (!dateOk) {
      const next = firstAvailable(days);
      if (next) setDate(next);
      else if (date) useDraft.setState({ date: null, slot: null });
    }
  }, [avail.data, dateOk, days, date, setDate]);

  const [shown, setShown] = useState<string | null>(null);
  const month = shown ?? (date ? monthOf(date) : from ? monthOf(firstAvailable(days) ?? from) : "");

  const slots = useQuery({
    queryKey: ["slots", tenant, locationId, specialistId, service?.id, date],
    queryFn: () => bookingApi.getSlots(tenant, { locationId, specialistId, serviceId: service!.id, date: date! }),
    enabled: !!location && !!service && !!date && dateOk,
  });

  const pick = (iso: string) => {
    // страховка: full/closed/past/поза горизонтом не вибираються
    if (days.find((d) => d.date === iso)?.status === "available") setDate(iso);
  };

  const masterName = specialistId === ANY_SPECIALIST_ID ? t("specialist.any") : (specialist?.name ?? "");

  return (
    <Screen>
      <H1>{service?.name ?? ""}</H1>

      <View style={s.master}>
        <Avatar name={masterName} photoUrl={specialist?.photoUrl} size={44} />
        <View style={{ flex: 1 }}>
          <Text style={s.masterLabel}>{t("time.master")}</Text>
          <Text style={s.masterName}>{masterName}</Text>
        </View>
        <Button title={t("time.change")} variant="secondary" onPress={() => router.back()} />
      </View>

      <Label>{t("time.date")}</Label>
      {avail.isLoading ? <Spinner /> : null}
      {avail.error ? (
        <>
          <ErrorText>{humanizeBookingError(avail.error)}</ErrorText>
          <Button title={t("common.retry")} onPress={() => void avail.refetch()} />
        </>
      ) : null}
      {avail.data && month ? (
        <MonthCalendar month={month} days={days} selected={dateOk ? date : null} onMonthChange={setShown} onSelect={pick} />
      ) : null}
      {avail.data && !firstAvailable(days) ? <Body muted>{t("time.nothingFree")}</Body> : null}
      {avail.data && firstAvailable(days) && !dateOk ? <Body muted>{t("time.pickHint")}</Body> : null}

      {dateOk && date ? <Label>{`${t("time.slots")} · ${longDate(date)}`}</Label> : null}
      {slots.isFetching ? <Spinner /> : null}
      {slots.error ? (
        <>
          <ErrorText>{humanizeBookingError(slots.error)}</ErrorText>
          <Button title={t("common.retry")} onPress={() => void slots.refetch()} />
        </>
      ) : null}
      {dateOk && !slots.isFetching && !slots.error && slots.data?.length === 0 ? <Body muted>{t("time.noSlots")}</Body> : null}
      <View style={{ flexDirection: "row", flexWrap: "wrap", gap: space.sm }}>
        {dateOk
          ? slots.data?.map((sl) => (
              <Chip
                key={sl.startsAt}
                label={sl.label}
                onPress={() => {
                  setSlot(sl);
                  router.push("/checkout");
                }}
              />
            ))
          : null}
      </View>
    </Screen>
  );
}

const s = StyleSheet.create({
  master: { flexDirection: "row", alignItems: "center", gap: space.md, backgroundColor: colors.surface, borderRadius: radius.md, borderWidth: 1, borderColor: colors.border, padding: space.md },
  masterLabel: { fontFamily: fonts.body, fontSize: 12, color: colors.muted },
  masterName: { fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text },
});
