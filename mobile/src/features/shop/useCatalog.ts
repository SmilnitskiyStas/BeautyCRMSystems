import { useEffect } from "react";
import { useQuery } from "@tanstack/react-query";
import { productOrdersApi } from "@/services";
import { useDraft } from "@/state/draft";
import { useCart } from "@/state/cart";

/** Каталог товарів обраного закладу (P1). Кеш 10 хв (§33.10 A/B); при кожному свіжому відповіді оновлює ціни/наявність у кошику. */
export function useShopCatalog() {
  const location = useDraft((d) => d.location);
  const q = useQuery({
    queryKey: ["shop-catalog", location?.tenantSlug, location?.locationId],
    queryFn: () => productOrdersApi.getCatalog(location!.tenantSlug, location!.locationId),
    enabled: !!location,
    staleTime: 10 * 60_000,
    retry: false,
  });
  useEffect(() => {
    const cart = useCart.getState();
    // кошик іншого закладу не чіпаємо: його очистить екран магазину після підтвердження
    if (q.data && cart.locationId === location?.locationId) cart.refresh(q.data.items);
  }, [q.data, location?.locationId]);
  return q;
}
