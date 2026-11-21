import { useCallback, useEffect, useRef, useState } from "react";
import { Alert, Text, View } from "react-native";
import { useLocalSearchParams, useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { BookingApiError } from "@beauty/booking-core";
import { localStore, productOrdersApi } from "@/services";
import { errorExtras, type PublicProductOrder } from "@/api/productOrders";
import { payWithReconcile } from "@/features/shop/pay";
import { shopCodeText, shopErrorText } from "@/features/shop/errors";
import { canReorder, formatLeft, statusText } from "@/features/shop/status";
import { useDraft } from "@/state/draft";
import { useCart } from "@/state/cart";
import { formatPrice } from "@/lib/format";
import { Body, Button, ErrorText, H1, Label, Screen, Spinner } from "@/ui";
import { colors, fonts, radius, space } from "@/theme";

const TERMINAL = new Set(["cancelled", "expired", "refunded", "fulfilled"]);
const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Статус замовлення (P3), оплата/скасування за `canPay`/`canCancel`. Токен читається лише в момент запиту й не потрапляє в навігацію/логи. */
export default function OrderScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const { id, notice: initialNotice } = useLocalSearchParams<{ id: string; notice?: string }>();

  const [order, setOrder] = useState<PublicProductOrder | null>(null);
  const [tenantSlug, setTenantSlug] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<string | null>(initialNotice ? shopCodeText(initialNotice) : null);
  const [now, setNow] = useState(Date.now());
  const reloaded = useRef<string | null>(null);
  const guard = useRef(false);

  const load = useCallback(async () => {
    const rec = (await localStore.listOrders()).find((r) => r.localId === id);
    const token = rec ? await localStore.getOrderToken(rec.localId) : null;
    if (!rec || !token) {
      setLoading(false);
      return router.replace("/order");
    }
    setTenantSlug(rec.tenantSlug);
    try {
      const o = await productOrdersApi.getOrder(rec.tenantSlug, token);
      await localStore.updateOrderSnapshot(rec.localId, { status: o.status });
      setOrder(o);
    } catch (e) {
      // 404 не доводить, що замовлення зникло: токен видаляємо лише для термінального локального статусу, інакше - повтор
      if (e instanceof BookingApiError && e.status === 404 && e.code === "not_found" && rec.status && TERMINAL.has(rec.status)) {
        await localStore.removeOrder(rec.localId);
        return router.replace("/order");
      }
      setMessage(shopErrorText(e));
    } finally {
      setLoading(false);
    }
  }, [id, router]);

  useEffect(() => {
    void load();
  }, [load]);

  const reserving = order?.status === "pending_payment" && !order.paymentInProgress && !!order.reservationExpiresAt;
  useEffect(() => {
    if (!reserving) return;
    const h = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(h);
  }, [reserving]);

  // резерв спливає за годинником пристрою -> один раз перепитати сервер (статус визначає лише він)
  useEffect(() => {
    if (reserving && order?.reservationExpiresAt && now >= new Date(order.reservationExpiresAt).getTime() && reloaded.current !== order.reservationExpiresAt) {
      reloaded.current = order.reservationExpiresAt;
      void load();
    }
  }, [now, reserving, order?.reservationExpiresAt, load]);

  const withToken = async (run: (slug: string, token: string) => Promise<void>) => {
    if (guard.current) return; // синхронно до першого await: подвійний тап не запускає паралельні P4
    guard.current = true;
    try {
      const token = id ? await localStore.getOrderToken(id) : null;
      if (!token || !tenantSlug) return;
      setBusy(true);
      setMessage(null);
      try {
        await run(tenantSlug, token);
      } finally {
        setBusy(false);
      }
    } finally {
      guard.current = false;
    }
  };

  const apply = async (o: PublicProductOrder) => {
    setOrder(o);
    if (id) await localStore.updateOrderSnapshot(id, { status: o.status });
  };

  const pay = () =>
    withToken(async (slug, token) => {
      const r = await payWithReconcile({ api: productOrdersApi, sleep }, slug, token);
      if (r.order) await apply(r.order);
      setMessage(r.errorCode ? shopCodeText(r.errorCode) : null);
    });

  const cancel = () =>
    Alert.alert(t("shop.cancelConfirmTitle"), undefined, [
      { text: t("shop.cancelConfirmNo"), style: "cancel" },
      {
        text: t("shop.cancelOrder"),
        style: "destructive",
        onPress: () =>
          void withToken(async (slug, token) => {
            try {
              await apply(await productOrdersApi.cancel(slug, token));
            } catch (e) {
              const o = errorExtras(e).order;
              if (o) await apply(o);
              setMessage(shopErrorText(e));
            }
          }),
      },
    ]);

  const reorder = () => {
    const loc = useDraft.getState().location;
    // кошик прив'язаний до закладу: заповнюємо лише коли цей заклад уже обрано; інакше - вибір закладу
    if (order && loc && loc.locationId === order.location.id) {
      useCart.getState().replace(loc.locationId, order.lines.map((l) => ({ productId: l.productId, name: l.name, unit: l.unit, price: l.unitPrice, quantity: l.quantity })));
      router.push("/shop");
    } else router.push("/");
  };

  if (loading) return <Spinner />;
  if (!order) return <Screen>{message ? <ErrorText>{message}</ErrorText> : null}</Screen>;

  return (
    <Screen>
      <H1>{t("shop.orderNumber", { n: order.number })}</H1>
      <Label>{order.location.name}</Label>
      <Body>{statusText(order, t as never, now)}</Body>
      {reserving && order.reservationExpiresAt ? <Body muted>{formatLeft(order.reservationExpiresAt, now)}</Body> : null}
      <View style={{ gap: space.xs, backgroundColor: colors.surface, borderRadius: radius.md, padding: space.md, borderWidth: 1, borderColor: colors.border }}>
        {order.lines.map((l) => (
          <View key={l.productId} style={{ flexDirection: "row", justifyContent: "space-between", gap: space.md }}>
            <Text style={{ flex: 1, fontFamily: fonts.body, fontSize: 15, color: colors.text }}>
              {l.name} × {l.quantity}
            </Text>
            <Text style={{ fontFamily: fonts.bodyMedium, fontSize: 15, color: colors.text }}>{formatPrice(l.lineTotal)}</Text>
          </View>
        ))}
        <Text style={{ fontFamily: fonts.heading, fontSize: 17, color: colors.text, textAlign: "right" }}>
          {t("shop.total")}: {formatPrice(order.total)}
        </Text>
      </View>
      {message ? <ErrorText>{message}</ErrorText> : null}
      {order.canPay ? <Button title={t("shop.payAgain")} onPress={() => void pay()} loading={busy} /> : null}
      {order.canCancel ? <Button title={t("shop.cancelOrder")} variant="danger" onPress={cancel} disabled={busy} /> : null}
      <Button title={t("shop.refresh")} variant="secondary" onPress={() => void load()} disabled={busy} />
      {canReorder(order) ? <Button title={t("shop.reorder")} variant="secondary" onPress={reorder} /> : null}
    </Screen>
  );
}
