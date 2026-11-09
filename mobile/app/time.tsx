import { useEffect, useMemo } from "react";
import { ScrollView, View } from "react-native";
import { useRouter } from "expo-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ANY_SPECIALIST_ID, humanizeBookingError } from "@beauty/booking-core";
import { dateOptions, longDate } from "@/lib/dates";
import { bookingApi } from "@/services";
import { useDraft } from "@/state/draft";
import { Body, Button, Chip, ErrorText, H1, Label, Screen, Spinner } from "@/ui";
import { space } from "@/theme";

/** Крок 3: спеціаліст («будь-який» за замовчуванням) + календар + вільні слоти. */
export default function TimeScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { location, service, specialistId, date } = useDraft();
  const { setSpecialist, setDate, setSlot } = useDraft.getState();

  useEffect(() => {
    if (!location || !service) router.replace("/");
  }, [location, service, router]);

  const tenant = location?.tenantSlug ?? "";
  const locationId = location?.locationId ?? "";

  const specialists = useQuery({
    queryKey: ["specialists", tenant, locationId, service?.id ?? null],
    queryFn: () => bookingApi.getSpecialists(tenant, locationId, service?.id),
    enabled: !!location,
  });
  // повний Location потрібен для вихідних/закриттів (resolve їх не віддає)
  const loc = useQuery({
    queryKey: ["location", tenant, locationId],
    queryFn: async () => (await bookingApi.getLocations(tenant)).find((l) => l.id === locationId) ?? null,
    enabled: !!location,
  });

  const days = useMemo(
    () => (location ? dateOptions(loc.data?.timezone ?? location.timezone, 14, new Date(), loc.data ?? undefined) : []),
    [location, loc.data],
  );

  // дата за замовчуванням - перший робочий день
  useEffect(() => {
    if (!date && days.length > 0) setDate((days.find((d) => !d.closed) ?? days[0]).iso);
  }, [date, days, setDate]);

  const slots = useQuery({
    queryKey: ["slots", tenant, locationId, specialistId, service?.id, date],
    queryFn: () => bookingApi.getSlots(tenant, { locationId, specialistId, serviceId: service!.id, date: date! }),
    enabled: !!location && !!service && !!date,
  });

  return (
    <Screen>
      <H1>{service?.name ?? ""}</H1>

      <Label>{t("time.specialist")}</Label>
      {specialists.isLoading ? <Spinner /> : null}
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={{ gap: space.sm }}>
        {specialists.data?.map((s) => (
          <Chip
            key={s.id}
            label={s.id === ANY_SPECIALIST_ID ? t("checkout.anySpecialist") : s.name}
            selected={specialistId === s.id}
            onPress={() => setSpecialist(s.id, s.id === ANY_SPECIALIST_ID ? "" : s.name)}
          />
        ))}
      </ScrollView>

      <Label>{t("time.date")}</Label>
      <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={{ gap: space.sm }}>
        {days.map((d) => (
          <Chip
            key={d.iso}
            label={`${d.weekday} ${d.label}`}
            selected={date === d.iso}
            disabled={d.closed}
            onPress={() => setDate(d.iso)}
          />
        ))}
      </ScrollView>

      {date ? <Label>{`${t("time.slots")} · ${longDate(date)}`}</Label> : null}
      {slots.isFetching ? <Spinner /> : null}
      {slots.error ? (
        <>
          <ErrorText>{humanizeBookingError(slots.error)}</ErrorText>
          <Button title={t("common.retry")} onPress={() => void slots.refetch()} />
        </>
      ) : null}
      {!slots.isFetching && slots.data?.length === 0 ? <Body muted>{t("time.noSlots")}</Body> : null}
      <View style={{ flexDirection: "row", flexWrap: "wrap", gap: space.sm }}>
        {slots.data?.map((s) => (
          <Chip
            key={s.startsAt}
            label={s.label}
            onPress={() => {
              setSlot(s);
              router.push("/checkout");
            }}
          />
        ))}
      </View>
    </Screen>
  );
}
