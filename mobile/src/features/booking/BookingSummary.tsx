import { Pressable, StyleSheet, Text, View } from "react-native";
import type { Service, Specialist } from "@beauty/booking-core";
import { useTranslation } from "react-i18next";
import type { ResolvedLocation } from "@/lib/locationSource";
import { formatPrice } from "@/lib/format";
import { shortDateLabel, timeLabel } from "@/lib/dates";
import { Avatar } from "./Avatar";
import { LocationLogo } from "@/features/location/LocationLogo";
import { locationAddress, specialistRatingText } from "./summary";
import { colors, fonts, radius, space, TOUCH } from "@/theme";

/** Картка «Ваш запис»: заклад, майстер (фото/рейтинг), послуга, дата й час. */
export function BookingSummary({
  location,
  service,
  specialist,
  startsAt,
  onChangeTime,
}: {
  location: ResolvedLocation;
  service: Service;
  specialist: Specialist | null;
  startsAt: string;
  onChangeTime?: () => void;
}) {
  const { t } = useTranslation();
  const address = locationAddress(location.address, location.city);
  const date = shortDateLabel(startsAt) ?? startsAt;
  const time = timeLabel(startsAt);
  return (
    <View style={s.card} accessibilityLabel={t("checkout.summaryTitle")}>
      <View style={s.head}>
        <View style={{ flexDirection: "row", alignItems: "center", gap: space.sm }}>
          <LocationLogo name={location.name} logoUrl={location.logoUrl} size={36} />
          <Text accessibilityRole="header" style={[s.title, { flex: 1 }]}>
            {location.name}
          </Text>
        </View>
        {address ? <Text style={s.muted}>{address}</Text> : null}
      </View>

      <View style={s.block}>
        {specialist ? (
          <Avatar name={specialist.name} photoUrl={specialist.photoUrl} size={52} />
        ) : (
          <View accessible={false} style={s.anyIcon}>
            <Text style={s.anyIconText}>👥</Text>
          </View>
        )}
        <View style={{ flex: 1, gap: 2 }}>
          <Text style={s.strong}>{specialist ? specialist.name : t("checkout.anySpecialistHint")}</Text>
          {specialist?.role ? <Text style={s.muted}>{specialist.role}</Text> : null}
          {specialist ? <Text style={s.rating}>{specialistRatingText(specialist.rating, specialist.reviewsCount, t("checkout.noReviews"))}</Text> : null}
        </View>
      </View>

      <View style={s.block}>
        <View style={{ flex: 1, gap: 2 }}>
          <Text style={s.strong}>{service.name}</Text>
          <Text style={s.muted}>{`${service.durationMinutes} ${t("common.minutes")}`}</Text>
        </View>
        <Text style={s.price}>{formatPrice(service.priceFinal)}</Text>
      </View>

      <View style={[s.block, { borderBottomWidth: 0 }]}>
        <Text accessible={false} style={s.icon}>
          📅
        </Text>
        <View style={{ flex: 1 }}>
          <Text style={s.strong}>{time ? `${date} · ${time}` : date}</Text>
        </View>
        {onChangeTime ? (
          <Pressable accessibilityRole="button" accessibilityLabel={t("checkout.changeTime")} onPress={onChangeTime} style={s.change}>
            <Text style={s.changeText}>{t("checkout.change")}</Text>
          </Pressable>
        ) : null}
      </View>
    </View>
  );
}

const s = StyleSheet.create({
  card: {
    backgroundColor: colors.surface,
    borderRadius: radius.lg,
    borderWidth: 1,
    borderColor: colors.border,
    overflow: "hidden",
    shadowColor: colors.text,
    shadowOpacity: 0.08,
    shadowRadius: 8,
    shadowOffset: { width: 0, height: 2 },
    elevation: 2,
  },
  head: { backgroundColor: colors.bg, padding: space.lg, gap: 2 },
  title: { fontFamily: fonts.heading, fontSize: 17, color: colors.text },
  block: { flexDirection: "row", alignItems: "center", gap: space.md, padding: space.lg, borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: colors.border },
  strong: { fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text },
  muted: { fontFamily: fonts.body, fontSize: 13, color: colors.muted },
  rating: { fontFamily: fonts.body, fontSize: 13, color: colors.primary },
  price: { fontFamily: fonts.heading, fontSize: 15, color: colors.primary },
  icon: { fontSize: 22 },
  anyIcon: { width: 52, height: 52, borderRadius: 26, backgroundColor: colors.bg, alignItems: "center", justifyContent: "center" },
  anyIconText: { fontSize: 24 },
  change: { minHeight: TOUCH, minWidth: TOUCH, paddingHorizontal: space.sm, alignItems: "center", justifyContent: "center" },
  changeText: { fontFamily: fonts.bodyMedium, fontSize: 14, color: colors.primary },
});
