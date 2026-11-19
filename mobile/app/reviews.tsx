import { StyleSheet, Text, View } from "react-native";
import { useLocalSearchParams } from "expo-router";
import { useInfiniteQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { humanizeBookingError } from "@beauty/booking-core";
import { bookingApi } from "@/services";
import { useDraft } from "@/state/draft";
import { uk } from "@/i18n/uk";
import { ratingLabel, reviewDate, stars } from "@/lib/specialist";
import { Body, Button, ErrorText, H1, Screen, Spinner } from "@/ui";
import { colors, fonts, radius, space } from "@/theme";

const PAGE = 20;

/** Відгуки про майстра (модалка): середній рейтинг, список, «Показати ще». */
export default function ReviewsScreen() {
  const { t } = useTranslation();
  const tenant = useDraft((d) => d.location?.tenantSlug ?? "");
  const p = useLocalSearchParams<{ specialistId: string; name?: string; rating?: string; count?: string }>();
  const specialistId = String(p.specialistId ?? "");
  const rating = p.rating ? Number(p.rating) : null;
  const count = Number(p.count ?? 0);

  const q = useInfiniteQuery({
    queryKey: ["reviews", tenant, specialistId],
    queryFn: ({ pageParam }) => bookingApi.getSpecialistReviews(tenant, specialistId, { limit: PAGE, offset: pageParam }),
    initialPageParam: 0,
    getNextPageParam: (last, all) => {
      const loaded = all.reduce((n, pg) => n + pg.items.length, 0);
      return last.items.length > 0 && loaded < last.total ? loaded : undefined;
    },
    enabled: !!tenant && !!specialistId,
  });

  const items = q.data?.pages.flatMap((pg) => pg.items) ?? [];
  const total = q.data?.pages[0]?.total ?? count;
  const summary = ratingLabel(rating, total);

  return (
    <Screen>
      <H1>{p.name ?? t("reviews.title")}</H1>
      {summary ? (
        <Text accessibilityLabel={`${t("reviews.average")}: ${summary}`} style={s.summary}>
          {summary}
        </Text>
      ) : null}
      {q.isLoading ? <Spinner /> : null}
      {q.error ? (
        <>
          <ErrorText>{humanizeBookingError(q.error)}</ErrorText>
          <Button title={t("common.retry")} onPress={() => void q.refetch()} />
        </>
      ) : null}
      {q.data && items.length === 0 ? <Body muted>{t("reviews.empty")}</Body> : null}
      {items.map((r) => (
        <View key={r.id} style={s.card}>
          <View style={s.head}>
            <Text style={s.author}>{r.authorName}</Text>
            <Text style={s.date}>{reviewDate(r.createdAt, uk.months)}</Text>
          </View>
          <Text accessibilityLabel={t("reviews.stars", { n: r.rating })} style={s.stars}>
            {stars(r.rating)}
          </Text>
          {r.text ? <Body>{r.text}</Body> : null}
        </View>
      ))}
      {q.hasNextPage ? (
        <Button title={t("reviews.more")} variant="secondary" loading={q.isFetchingNextPage} onPress={() => void q.fetchNextPage()} />
      ) : null}
    </Screen>
  );
}

const s = StyleSheet.create({
  summary: { fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text },
  card: { backgroundColor: colors.surface, borderRadius: radius.md, borderWidth: 1, borderColor: colors.border, padding: space.md, gap: space.xs },
  head: { flexDirection: "row", justifyContent: "space-between", gap: space.sm },
  author: { fontFamily: fonts.bodyMedium, fontSize: 15, color: colors.text },
  date: { fontFamily: fonts.body, fontSize: 13, color: colors.muted },
  stars: { fontSize: 16, color: colors.primary },
});
