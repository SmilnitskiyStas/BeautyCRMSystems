import { useCallback, useState } from "react";
import { Alert } from "react-native";
import { useFocusEffect, useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { localStore } from "@/services";
import type { OrderRecord } from "@/storage/tokenStore";
import { formatPrice } from "@/lib/format";
import { Body, Button, H1, Row, Screen, Spinner } from "@/ui";

/** «Мої замовлення»: локальний індекс (без токенів); деталь і актуальний статус - на екрані замовлення (P3). */
export default function OrdersScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const [items, setItems] = useState<OrderRecord[] | null>(null);

  useFocusEffect(
    useCallback(() => {
      void localStore.listOrders().then(setItems);
    }, []),
  );

  const wipe = () =>
    Alert.alert(t("appointments.wipeConfirmTitle"), t("appointments.wipeConfirmBody"), [
      { text: t("common.cancel"), style: "cancel" },
      {
        text: t("appointments.wipeConfirmYes"),
        style: "destructive",
        onPress: async () => {
          try {
            await localStore.wipeAll();
          } catch {
            return Alert.alert(t("appointments.wipeFailed"));
          }
          setItems([]);
          Alert.alert(t("appointments.wiped"));
        },
      },
    ]);

  return (
    <Screen>
      <H1>{t("shop.myOrders")}</H1>
      {items === null ? <Spinner /> : null}
      {items?.length === 0 ? <Body muted>{t("shop.ordersEmpty")}</Body> : null}
      {items?.map((r) => (
        <Row
          key={r.localId}
          title={t("shop.orderNumber", { n: r.number })}
          subtitle={r.locationName}
          right={formatPrice(r.total)}
          onPress={() => router.push({ pathname: "/order/[id]", params: { id: r.localId } })}
        />
      ))}
      <Button title={t("appointments.wipeAction")} variant="danger" onPress={wipe} />
    </Screen>
  );
}
