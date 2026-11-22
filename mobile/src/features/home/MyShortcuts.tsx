import { useCallback, useState } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { useFocusEffect, useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { localStore } from "@/services";
import type { AppointmentRecord, OrderRecord } from "@/storage/tokenStore";
import { formatWhen } from "@/lib/format";
import { colors, fonts, radius, space, TOUCH } from "@/theme";
import { activeOrdersCount, nextAppointment } from "./shortcuts";

/**
 * Два помітні входи вгорі головної: «Мої записи» (з найближчим записом) і «Мої замовлення» (з кількістю активних).
 * Дані - лише локальний індекс (без токенів і без мережі), оновлюється при кожному поверненні на екран.
 */
export function MyShortcuts() {
  const { t } = useTranslation();
  const router = useRouter();
  const [appts, setAppts] = useState<AppointmentRecord[] | null>(null);
  const [orders, setOrders] = useState<OrderRecord[] | null>(null);

  useFocusEffect(
    useCallback(() => {
      void localStore.listAppointments().then(setAppts).catch(() => setAppts([]));
      void localStore.listOrders().then(setOrders).catch(() => setOrders([]));
    }, []),
  );

  const next = appts ? nextAppointment(appts) : null;
  const apptSub = !appts
    ? " "
    : next
      ? t("home.nextAppointment", { when: formatWhen(next.startsAt) })
      : appts.length > 0
        ? t("home.appointmentsCount", { n: appts.length })
        : t("home.nothingYet");

  const active = orders ? activeOrdersCount(orders) : 0;
  const orderSub = !orders
    ? " "
    : active > 0
      ? t("home.activeOrders", { n: active })
      : orders.length > 0
        ? t("home.ordersCount", { n: orders.length })
        : t("home.nothingYet");

  return (
    <View style={s.row}>
      <Tile title={t("common.myAppointments")} subtitle={apptSub} highlight={!!next} onPress={() => router.push("/appointments")} />
      <Tile title={t("shop.myOrders")} subtitle={orderSub} highlight={active > 0} onPress={() => router.push("/order")} />
    </View>
  );
}

function Tile({ title, subtitle, highlight, onPress }: { title: string; subtitle: string; highlight: boolean; onPress: () => void }) {
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={`${title}. ${subtitle}`}
      onPress={onPress}
      style={({ pressed }) => [s.tile, highlight && s.tileOn, pressed && { opacity: 0.8 }]}
    >
      <Text style={[s.title, highlight && { color: colors.onPrimary }]} numberOfLines={1}>
        {title}
      </Text>
      <Text style={[s.sub, highlight && { color: colors.onPrimary }]} numberOfLines={2}>
        {subtitle}
      </Text>
    </Pressable>
  );
}

const s = StyleSheet.create({
  row: { flexDirection: "row", gap: space.sm },
  tile: {
    flex: 1,
    minHeight: TOUCH + space.xl,
    borderRadius: radius.md,
    borderWidth: 1,
    borderColor: colors.primary,
    backgroundColor: colors.surface,
    padding: space.md,
    gap: space.xs,
    justifyContent: "center",
  },
  tileOn: { backgroundColor: colors.primary },
  title: { fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.primary },
  sub: { fontFamily: fonts.body, fontSize: 13, color: colors.muted },
});
