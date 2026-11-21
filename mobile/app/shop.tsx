import { useEffect, useMemo, useState } from "react";
import { Alert, Pressable, Text, View } from "react-native";
import { useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { useDraft } from "@/state/draft";
import { DEFAULT_LIMITS, cartCount, cartTotal, useCart } from "@/state/cart";
import { useShopCatalog } from "@/features/shop/useCatalog";
import { Stepper } from "@/features/shop/Stepper";
import { isShopDisabled, shopErrorText } from "@/features/shop/errors";
import { formatPrice } from "@/lib/format";
import { Body, Button, ErrorText, H1, Label, Screen, Spinner } from "@/ui";
import { colors, fonts, radius, space } from "@/theme";

/** Каталог товарів вибраного закладу (P1), групування за `categoryPath`; кошик прив'язаний до закладу. */
export default function ShopScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const location = useDraft((d) => d.location);
  const q = useShopCatalog();
  const lines = useCart((c) => c.lines);
  const [notice, setNotice] = useState<string | null>(null);
  const [bound, setBound] = useState(false);

  useEffect(() => {
    if (!location) router.replace("/");
  }, [location, router]);

  // інший заклад у кошику: підтвердження й очищення (§33.10 B)
  useEffect(() => {
    if (!location) return;
    const cart = useCart.getState();
    if (cart.locationId === location.locationId || cart.lines.length === 0) {
      cart.bind(location.locationId);
      setBound(true);
      return;
    }
    Alert.alert(t("shop.switchTitle"), t("shop.switchBody"), [
      { text: t("common.cancel"), style: "cancel", onPress: () => router.back() },
      {
        text: t("shop.switchYes"),
        style: "destructive",
        onPress: () => {
          cart.bind(location.locationId);
          setBound(true);
        },
      },
    ]);
  }, [location, router, t]);

  const limits = q.data ? { maxLines: q.data.maxLines, maxQuantityPerLine: q.data.maxQuantityPerLine } : DEFAULT_LIMITS;
  const groups = useMemo(() => {
    const m = new Map<string, NonNullable<typeof q.data>["items"]>();
    for (const p of q.data?.items ?? []) {
      const k = p.categoryPath ?? t("shop.other");
      m.set(k, [...(m.get(k) ?? []), p]);
    }
    return [...m.entries()];
  }, [q.data, t]);

  const qty = (id: string) => lines.find((l) => l.productId === id)?.quantity ?? 0;
  const change = (id: string, delta: number, add: () => boolean) => {
    setNotice(null);
    if (delta > 0 && !add()) setNotice(t("shop.limitReached", { n: limits.maxLines }));
    else if (delta < 0) useCart.getState().setQuantity(id, qty(id) - 1, limits);
  };

  if (!location) return null;
  const count = cartCount(lines);

  return (
    <Screen>
      <H1>{t("shop.title")}</H1>
      <Body muted>{location.name}</Body>
      <Button title={t("shop.cartButton", { count, sum: formatPrice(cartTotal(lines)) })} onPress={() => router.push("/cart")} disabled={count === 0} />
      <Button title={t("shop.myOrders")} variant="secondary" onPress={() => router.push("/order")} />
      {q.isLoading || !bound ? <Spinner /> : null}
      {q.error ? (
        isShopDisabled(q.error) ? (
          <Body muted>{t("shop.disabled")}</Body>
        ) : (
          <>
            <ErrorText>{shopErrorText(q.error)}</ErrorText>
            <Button title={t("common.retry")} onPress={() => void q.refetch()} />
          </>
        )
      ) : null}
      {notice ? <ErrorText>{notice}</ErrorText> : null}
      {q.data?.items.length === 0 ? <Body muted>{t("shop.empty")}</Body> : null}
      {bound
        ? groups.map(([category, items]) => (
            <View key={category} style={{ gap: space.sm }}>
              <Label>{category}</Label>
              {items.map((p) => {
                const n = qty(p.id);
                return (
                  <View key={p.id} style={{ flexDirection: "row", alignItems: "center", gap: space.md, backgroundColor: colors.surface, borderRadius: radius.md, padding: space.md, borderWidth: 1, borderColor: colors.border }}>
                    <View style={{ flex: 1 }}>
                      <Text style={{ fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text }}>{p.name}</Text>
                      <Text style={{ fontFamily: fonts.body, fontSize: 13, color: p.inStock ? colors.muted : colors.danger }}>
                        {p.inStock ? formatPrice(p.price) : t("shop.outOfStock")}
                      </Text>
                    </View>
                    {n > 0 ? (
                      <View style={{ flexDirection: "row", alignItems: "center", gap: space.sm }}>
                        <Stepper label="−" a11y={t("shop.dec", { name: p.name })} onPress={() => change(p.id, -1, () => true)} />
                        <Text style={{ fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text, minWidth: 20, textAlign: "center" }}>{n}</Text>
                        <Stepper label="+" a11y={t("shop.inc", { name: p.name })} onPress={() => change(p.id, 1, () => useCart.getState().add(p, limits))} />
                      </View>
                    ) : (
                      <Stepper label={t("shop.add")} a11y={t("shop.addA11y", { name: p.name })} disabled={!p.inStock} wide onPress={() => change(p.id, 1, () => useCart.getState().add(p, limits))} />
                    )}
                  </View>
                );
              })}
            </View>
          ))
        : null}
      {q.data?.truncated ? <Body muted>{t("shop.truncated")}</Body> : null}
    </Screen>
  );
}
