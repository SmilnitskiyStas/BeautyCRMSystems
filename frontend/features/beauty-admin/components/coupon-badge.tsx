"use client";

import { useAuth } from "@/features/beauty-auth/components/auth-provider";
import { canSeeCouponHint } from "@/features/beauty-auth/permissions";
import { money } from "../format";
import { hintLabel } from "../marketing/logic";
import type { Appointment } from "../types";
import { Badge } from "./ui";

/**
 * Позначка «Записано за купоном» на записі (§26.11.4): назва акції купона й знижка. Повного коду немає ніколи;
 * `codeHint` (останні 3 символи) бачать лише owner/admin - для specialist у DOM його немає.
 */
export function CouponBadge({ appt }: { appt: Pick<Appointment, "coupon" | "priceFinal" | "priceOriginal"> }) {
  const { user } = useAuth();
  if (!appt.coupon || appt.coupon.outcome !== "applied") return null;
  const saved = Math.max(0, (appt.priceOriginal ?? appt.priceFinal) - appt.priceFinal);
  const hint = canSeeCouponHint(user.role) ? appt.coupon.codeHint : undefined;
  return (
    <div data-testid="coupon-badge" className="flex flex-col gap-0.5">
      <Badge tone="vip" className="self-start">Записано за купоном</Badge>
      <span className="text-[13px] text-(--muted)">
        {appt.coupon.promotionName}
        {saved > 0 ? `, знижка −${money(saved)}` : ""}
        {hint ? ` · код ${hintLabel(hint)}` : ""}
      </span>
    </div>
  );
}
