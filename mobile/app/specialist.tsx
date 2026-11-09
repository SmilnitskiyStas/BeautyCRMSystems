import { useEffect } from "react";
import { Pressable, StyleSheet, Text, View } from "react-native";
import { useRouter } from "expo-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ANY_SPECIALIST_ID, humanizeBookingError, type Specialist } from "@beauty/booking-core";
import { bookingApi } from "@/services";
import { useDraft } from "@/state/draft";
import { Avatar } from "@/features/booking/Avatar";
import { experienceLabel, ratingLabel } from "@/lib/specialist";
import { Body, Button, ErrorText, H1, Screen, Spinner } from "@/ui";
import { colors, fonts, radius, space, TOUCH } from "@/theme";

/** Крок 3: майстер («будь-який» першим). */
export default function SpecialistScreen() {
  const { t } = useTranslation();
  const router = useRouter();
  const location = useDraft((d) => d.location);
  const service = useDraft((d) => d.service);
  const selectedId = useDraft((d) => d.specialistId);
  const setSpecialist = useDraft((d) => d.setSpecialist);

  useEffect(() => {
    if (!location || !service) router.replace("/");
  }, [location, service, router]);

  const q = useQuery({
    queryKey: ["specialists", location?.tenantSlug, location?.locationId, service?.id ?? null],
    queryFn: () => bookingApi.getSpecialists(location!.tenantSlug, location!.locationId, service?.id),
    enabled: !!location && !!service,
  });

  const choose = (sp: Specialist | null) => {
    setSpecialist(sp);
    router.push("/time");
  };
  const masters = (q.data ?? []).filter((s) => s.id !== ANY_SPECIALIST_ID);

  return (
    <Screen>
      <H1>{service?.name ?? ""}</H1>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={t("specialist.any")}
        accessibilityState={{ selected: selectedId === ANY_SPECIALIST_ID }}
        onPress={() => choose(null)}
        style={[s.card, selectedId === ANY_SPECIALIST_ID && s.cardOn]}
      >
        <View style={s.anyIcon}>
          <Text style={s.anyIconText}>★</Text>
        </View>
        <View style={{ flex: 1 }}>
          <Text style={s.name}>{t("specialist.any")}</Text>
          <Text style={s.sub}>{t("specialist.anyHint")}</Text>
        </View>
      </Pressable>

      {q.isLoading ? <Spinner /> : null}
      {q.error ? (
        <>
          <ErrorText>{humanizeBookingError(q.error)}</ErrorText>
          <Button title={t("common.retry")} onPress={() => void q.refetch()} />
        </>
      ) : null}
      {q.data && masters.length === 0 ? <Body muted>{t("specialist.empty")}</Body> : null}

      {masters.map((m) => {
        const rating = ratingLabel(m.rating, m.reviewsCount);
        const exp = experienceLabel(m.experienceYears);
        return (
          <View key={m.id} style={[s.cardWrap, selectedId === m.id && s.cardOn]}>
            <Pressable
              accessibilityRole="button"
              accessibilityLabel={t("specialist.pick", { name: m.name })}
              accessibilityState={{ selected: selectedId === m.id }}
              onPress={() => choose(m)}
              style={s.card2}
            >
              <Avatar name={m.name} photoUrl={m.photoUrl} />
              <View style={{ flex: 1, gap: 2 }}>
                <Text style={s.name}>{m.name}</Text>
                {m.role ? <Text style={s.sub}>{m.role}</Text> : null}
                <Text style={s.meta}>{rating ?? t("specialist.noReviews")}</Text>
                {exp ? <Text style={s.sub}>{exp}</Text> : null}
                {m.bio ? (
                  <Text numberOfLines={2} style={s.sub}>
                    {m.bio}
                  </Text>
                ) : null}
              </View>
            </Pressable>
            <Pressable
              accessibilityRole="link"
              accessibilityLabel={t("specialist.reviewsA11y", { name: m.name })}
              onPress={() =>
                router.push({
                  pathname: "/reviews",
                  params: { specialistId: m.id, name: m.name, rating: m.rating == null ? "" : String(m.rating), count: String(m.reviewsCount ?? 0) },
                })
              }
              style={s.reviewsLink}
            >
              <Text style={s.linkText}>{t("specialist.reviews")}</Text>
            </Pressable>
          </View>
        );
      })}
    </Screen>
  );
}

const s = StyleSheet.create({
  card: { minHeight: 72, flexDirection: "row", alignItems: "center", gap: space.md, backgroundColor: colors.surface, borderRadius: radius.md, padding: space.md, borderWidth: 1, borderColor: colors.border },
  cardWrap: { backgroundColor: colors.surface, borderRadius: radius.md, borderWidth: 1, borderColor: colors.border },
  cardOn: { borderColor: colors.primary, borderWidth: 2 },
  card2: { flexDirection: "row", gap: space.md, padding: space.md, alignItems: "flex-start" },
  anyIcon: { width: 56, height: 56, borderRadius: 28, backgroundColor: colors.bg, borderWidth: 1, borderColor: colors.border, alignItems: "center", justifyContent: "center" },
  anyIconText: { fontSize: 22, color: colors.primary },
  name: { fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text },
  sub: { fontFamily: fonts.body, fontSize: 13, color: colors.muted },
  meta: { fontFamily: fonts.bodyMedium, fontSize: 13, color: colors.text },
  reviewsLink: { minHeight: TOUCH, justifyContent: "center", paddingHorizontal: space.md, borderTopWidth: 1, borderTopColor: colors.border },
  linkText: { fontFamily: fonts.bodyMedium, fontSize: 14, color: colors.primary },
});
