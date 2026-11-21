import { useEffect } from "react";
import { Text, View } from "react-native";
import { useRouter } from "expo-router";
import { useTranslation } from "react-i18next";
import { useDraft } from "@/state/draft";
import { DEFAULT_LIMITS, cartTotal, useCart } from "@/state/cart";
import { useShopCatalog } from "@/features/shop/useCatalog";
import { Stepper } from "@/features/shop/Stepper";
import { formatPrice } from "@/lib/format";
import { Body, Button, ErrorText, H1, Screen } from "@/ui";
import { colors, fonts, radius, space } from "@/theme";

/** Кошик: рядки, кількість, сума. Ціни з останнього P1; кеш старший за 10 хв оновлюється при відкритті (staleTime у useShopCatalog). */
export default function CartScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const location = useDraft((d) => d.location);
  const lines = useCart((c) => c.lines);
  const q = useShopCatalog();
  const limits = q.data ? { maxLines: q.data.maxLines, maxQuantityPerLine: q.data.maxQuantityPerLine } : DEFAULT_LIMITS;

  useEffect(() => {
    if (!location) router.replace("/");
  }, [location, router]);

  const blocked = lines.some((l) => l.unavailable);

  return (
    <Screen>
      <H1>{t("shop.cart")}</H1>
      {lines.length === 0 ? <Body muted>{t("shop.cartEmpty")}</Body> : null}
      {lines.map((l) => (
        <View key={l.productId} style={{ gap: space.xs, backgroundColor: colors.surface, borderRadius: radius.md, padding: space.md, borderWidth: 1, borderColor: l.unavailable ? colors.danger : colors.border }}>
          <Text style={{ fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text }}>{l.name}</Text>
          {l.unavailable ? <ErrorText>{t("shop.unavailableLine")}</ErrorText> : null}
          <View style={{ flexDirection: "row", alignItems: "center", gap: space.sm }}>
            <Stepper label="−" a11y={t("shop.dec", { name: l.name })} onPress={() => useCart.getState().setQuantity(l.productId, l.quantity - 1, limits)} />
            <Text style={{ fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text, minWidth: 24, textAlign: "center" }}>{l.quantity}</Text>
            <Stepper label="+" a11y={t("shop.inc", { name: l.name })} onPress={() => useCart.getState().setQuantity(l.productId, l.quantity + 1, limits)} />
            <Text style={{ flex: 1, textAlign: "right", fontFamily: fonts.bodyMedium, fontSize: 15, color: colors.primary }}>{formatPrice((Math.round(l.price * 100) * l.quantity) / 100)}</Text>
            <Stepper label="✕" a11y={t("shop.remove", { name: l.name })} onPress={() => useCart.getState().remove(l.productId)} />
          </View>
        </View>
      ))}
      {lines.length > 0 ? (
        <>
          <Text style={{ fontFamily: fonts.heading, fontSize: 18, color: colors.text }}>
            {t("shop.total")}: {formatPrice(cartTotal(lines))}
          </Text>
          {blocked ? <ErrorText>{t("shop.removeUnavailable")}</ErrorText> : null}
          <Button title={t("shop.checkout")} onPress={() => router.push("/order-checkout")} disabled={blocked} />
        </>
      ) : (
        <Button title={t("shop.title")} variant="secondary" onPress={() => router.back()} />
      )}
    </Screen>
  );
}
