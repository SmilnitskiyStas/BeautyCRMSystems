import { useEffect, useRef, useState } from "react";
import { View } from "react-native";
import { useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { BookingApiError, clientSchema } from "@beauty/booking-core";
import { localStore, orderSubmitter, productOrdersApi } from "@/services";
import { errorExtras } from "@/api/productOrders";
import { isRetryable } from "@/features/booking/submit";
import { payWithReconcile } from "@/features/shop/pay";
import { shopErrorText } from "@/features/shop/errors";
import { useShopCatalog } from "@/features/shop/useCatalog";
import { useDraft } from "@/state/draft";
import { cartTotal, useCart } from "@/state/cart";
import { formatPrice } from "@/lib/format";
import { Body, Button, ErrorText, Field, H1, Label, Screen, Spinner } from "@/ui";
import { space } from "@/theme";

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Оформлення: ім'я/телефон (без email і згод) -> P2 -> одразу P4 (mock-оплата) -> екран статусу. */
export default function OrderCheckoutScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const location = useDraft((d) => d.location);
  const lines = useCart((c) => c.lines);
  const catalog = useShopCatalog();

  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [errors, setErrors] = useState<{ name?: string; phone?: string }>({});
  const [phase, setPhase] = useState<"idle" | "creating" | "paying">("idle");
  const [error, setError] = useState<string | null>(null);

  const submitting = useRef(false);
  const finished = useRef(false); // кошик очищено після створення замовлення - не редіректити на головну
  useEffect(() => {
    if (!finished.current && (!location || lines.length === 0)) router.replace("/");
  }, [location, lines.length, router]);

  useEffect(() => {
    void localStore.getProfile().then((p) => {
      if (p) {
        setName((n) => n || p.name);
        setPhone((v) => v || p.phone);
      }
    });
  }, []);

  if (finished.current) {
    return (
      <Screen>
        <H1>{t("shop.paying")}</H1>
        <Spinner />
      </Screen>
    );
  }
  if (!location || lines.length === 0) return null;
  const total = cartTotal(lines);
  const busy = phase !== "idle";

  const backToCart = () => router.back();

  const submit = async () => {
    if (submitting.current) return; // синхронно до першого await: подвійний тап не запускає паралельні P2/P4
    submitting.current = true;
    try {
      await doSubmit();
    } finally {
      submitting.current = false;
    }
  };

  const doSubmit = async () => {
    const parsed = clientSchema.pick({ name: true, phone: true }).safeParse({ name, phone });
    if (!parsed.success) {
      const next: typeof errors = {};
      for (const i of parsed.error.issues) next[i.path[0] as "name" | "phone"] ??= i.message;
      return setErrors(next);
    }
    setErrors({});
    setError(null);
    setPhase("creating");
    let created;
    try {
      created = await orderSubmitter.submit(
        location.tenantSlug,
        {
          locationId: location.locationId,
          customer: { name: parsed.data.name, phone: parsed.data.phone },
          lines: lines.map((l) => ({ productId: l.productId, quantity: l.quantity })),
          expectedTotal: total,
        },
        location.name,
      );
    } catch (e) {
      setPhase("idle");
      return void handleCreateError(e);
    }

    // замовлення створено й токен збережено: кошик більше не потрібен
    finished.current = true;
    useCart.getState().clear();
    const p = await localStore.getProfile().catch(() => null);
    await localStore.setProfile({ name: parsed.data.name, phone: parsed.data.phone, email: p?.email }).catch(() => undefined);

    setPhase("paying");
    const token = await localStore.getOrderToken(created.localId);
    let notice = "";
    if (token && created.order.status === "pending_payment") {
      const outcome = await payWithReconcile({ api: productOrdersApi, sleep }, location.tenantSlug, token);
      if (outcome.errorCode) notice = outcome.errorCode;
      if (outcome.order) await localStore.updateOrderSnapshot(created.localId, { status: outcome.order.status }).catch(() => undefined);
    }
    router.replace({ pathname: "/order/[id]", params: { id: created.localId, ...(notice ? { notice } : {}) } });
  };

  const handleCreateError = (e: unknown) => {
    if (!(e instanceof BookingApiError)) return setError(shopErrorText(e));
    const x = errorExtras(e);
    switch (e.code) {
      case "insufficient_stock":
        useCart.getState().markUnavailable(x.productIds ?? []);
        void catalog.refetch();
        setError(shopErrorText(e));
        return backToCart();
      case "product_unavailable":
        useCart.getState().removeMany(x.productIds ?? []);
        void catalog.refetch();
        setError(t("shop.removedUnavailable"));
        return backToCart();
      case "price_changed":
        // новий P1 оновить ціни в кошику; наступна відправка матиме іншу суму -> новий Idempotency-Key
        void catalog.refetch();
        return setError(x.total !== undefined ? t("shop.priceChanged", { sum: formatPrice(x.total) }) : shopErrorText(e));
      case "invalid_name":
        return setErrors({ name: shopErrorText(e) });
      case "invalid_phone":
        return setErrors({ phone: shopErrorText(e) });
      default:
        return setError(isRetryable(e) ? `${shopErrorText(e)} ${t("shop.retryHint")}` : shopErrorText(e));
    }
  };

  return (
    <Screen>
      <H1>{t("shop.checkoutTitle")}</H1>
      <View style={{ gap: space.xs }}>
        <Label>{location.name}</Label>
        {location.address ? <Body muted>{location.address}</Body> : null}
        <Body>
          {t("shop.total")}: {formatPrice(total)}
        </Body>
      </View>
      <Field label={t("shop.name")} value={name} onChangeText={setName} autoComplete="name" textContentType="name" error={errors.name} />
      <Field
        label={t("shop.phone")}
        placeholder={t("shop.phonePlaceholder")}
        value={phone}
        onChangeText={setPhone}
        keyboardType="phone-pad"
        autoComplete="tel"
        textContentType="telephoneNumber"
        error={errors.phone}
      />
      <Body muted>{t("shop.paymentInfo", { m: catalog.data?.reservationMinutes ?? 15 })}</Body>
      {error ? <ErrorText>{error}</ErrorText> : null}
      <Button title={phase === "paying" ? t("shop.paying") : t("shop.pay")} onPress={() => void submit()} loading={busy} />
    </Screen>
  );
}
