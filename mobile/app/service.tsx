import { useEffect } from "react";
import { useRouter } from "expo-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ANY_SPECIALIST_ID, humanizeBookingError } from "@beauty/booking-core";
import { bookingApi } from "@/services";
import { useDraft } from "@/state/draft";
import { useShopCatalog } from "@/features/shop/useCatalog";
import { formatPrice } from "@/lib/format";
import { Body, Button, ErrorText, H1, Row, Screen, Spinner } from "@/ui";

/** Крок 2: послуга. */
export default function ServiceScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const location = useDraft((d) => d.location);
  const setService = useDraft((d) => d.setService);

  useEffect(() => {
    if (!location) router.replace("/");
  }, [location, router]);

  const q = useQuery({
    queryKey: ["services", location?.tenantSlug, location?.locationId],
    queryFn: () => bookingApi.getServices(location!.tenantSlug, location!.locationId, ANY_SPECIALIST_ID),
    enabled: !!location,
  });

  const shop = useShopCatalog();

  return (
    <Screen>
      <H1>{location?.name ?? ""}</H1>
      <Button title={t("service.change")} variant="secondary" onPress={() => router.push("/?change=1")} />
      {/* «Магазин» показуємо лише коли P1 відповів 200; 404 product_orders_disabled/помилка -> розділу немає (§33.10 A) */}
      {shop.data ? <Button title={t("shop.entry")} variant="secondary" onPress={() => router.push("/shop")} /> : null}
      {q.isLoading ? <Spinner /> : null}
      {q.error ? (
        <>
          <ErrorText>{humanizeBookingError(q.error)}</ErrorText>
          <Button title={t("common.retry")} onPress={() => void q.refetch()} />
        </>
      ) : null}
      {q.data?.length === 0 ? <Body muted>{t("service.empty")}</Body> : null}
      {q.data?.map((s) => (
        <Row
          key={s.id}
          title={s.name}
          subtitle={[`${s.durationMinutes} ${t("common.minutes")}`, s.promoLabel].filter(Boolean).join(" · ")}
          right={formatPrice(s.priceFinal)}
          onPress={() => {
            setService(s);
            router.push("/specialist");
          }}
        />
      ))}
    </Screen>
  );
}
