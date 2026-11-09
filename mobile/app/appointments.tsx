import { useState } from "react";
import { Alert, View } from "react-native";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { humanizeBookingError, type CancelResult } from "@beauty/booking-core";
import { bookingApi, localStore } from "@/services";
import type { AppointmentRecord } from "@/storage/tokenStore";
import { cancellationLines, formatPrice, formatWhen } from "@/lib/format";
import { Body, Button, ErrorText, H1, Label, Screen, Spinner } from "@/ui";
import { colors, fonts, radius, space } from "@/theme";
import { Text } from "react-native";

/** «Мої записи»: індекс без токенів; статус оновлюється з сервера (джерело істини), офлайн - знімок із позначкою. */
export default function AppointmentsScreen() {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const list = useQuery({ queryKey: ["local-appointments"], queryFn: () => localStore.listAppointments(), staleTime: 0 });

  const wipe = () =>
    Alert.alert(t("appointments.wipeConfirmTitle"), t("appointments.wipeConfirmBody"), [
      { text: t("common.cancel"), style: "cancel" },
      {
        text: t("appointments.wipeConfirmYes"),
        style: "destructive",
        onPress: async () => {
          await localStore.wipeAll();
          qc.clear();
          await list.refetch();
          Alert.alert(t("appointments.wiped"));
        },
      },
    ]);

  return (
    <Screen>
      <H1>{t("appointments.title")}</H1>
      {list.isLoading ? <Spinner /> : null}
      {list.data?.length === 0 ? <Body muted>{t("appointments.empty")}</Body> : null}
      {list.data?.map((r) => <AppointmentCard key={r.localId} record={r} />)}
      <Button title={t("appointments.wipeAction")} variant="danger" onPress={wipe} />
    </Screen>
  );
}

function AppointmentCard({ record }: { record: AppointmentRecord }) {
  const { t } = useTranslation();
  const qc = useQueryClient();
  const [result, setResult] = useState<CancelResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const q = useQuery({
    queryKey: ["appointment", record.localId],
    queryFn: async () => {
      const token = await localStore.getToken(record.localId);
      if (!token) throw new Error("no_token");
      const a = await bookingApi.getAppointment(record.tenantSlug, token);
      await localStore.updateSnapshot(record.localId, { status: a.status, startsAt: a.startsAt });
      return a;
    },
    staleTime: 0,
  });

  const a = result?.appointment ?? q.data;
  const stale = !a; // сервер недоступний -> показуємо локальний знімок
  const status = a?.status ?? record.status ?? "pending";
  const startsAt = a?.startsAt ?? record.startsAt;
  const canCancel = !!a && (status === "pending" || status === "confirmed");

  const doCancel = async () => {
    setBusy(true);
    setError(null);
    try {
      const token = await localStore.getToken(record.localId);
      if (!token) throw new Error("no_token");
      const r = await bookingApi.cancelAppointment(record.tenantSlug, token);
      await localStore.updateSnapshot(record.localId, { status: r.appointment.status });
      setResult(r);
      void qc.invalidateQueries({ queryKey: ["appointment", record.localId] });
    } catch (e) {
      setError(humanizeBookingError(e));
    } finally {
      setBusy(false);
    }
  };

  const confirmCancel = () =>
    Alert.alert(t("appointments.cancelConfirmTitle"), cancellationLines(a?.cancellation, t as never).join("\n"), [
      { text: t("appointments.cancelConfirmNo"), style: "cancel" },
      { text: t("appointments.cancelConfirmYes"), style: "destructive", onPress: () => void doCancel() },
    ]);

  return (
    <View style={{ gap: space.xs, backgroundColor: colors.surface, borderRadius: radius.md, padding: space.md, borderWidth: 1, borderColor: colors.border }}>
      <Text style={{ fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text }}>{a?.serviceName ?? record.serviceName}</Text>
      <Body>{`${a?.locationName ?? record.locationName} · ${formatWhen(startsAt)}`}</Body>
      <Label>{t(`appointments.status.${status}`, { defaultValue: status })}</Label>
      {a ? <Body muted>{formatPrice(a.priceFinal)}</Body> : null}
      {stale && !q.isLoading ? <Body muted>{t("appointments.stale")}</Body> : null}
      {result ? (
        <Body>
          {`${t("appointments.cancelled")}. ${t("appointments.refund", { amount: result.refundAmount, percent: result.refundPercent })}`}
        </Body>
      ) : null}
      {error ? <ErrorText>{error}</ErrorText> : null}
      {canCancel && !result ? <Button title={t("appointments.cancelAction")} variant="danger" onPress={confirmCancel} loading={busy} /> : null}
    </View>
  );
}
