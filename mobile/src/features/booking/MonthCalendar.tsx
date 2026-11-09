import { Pressable, StyleSheet, Text, View } from "react-native";
import { useTranslation } from "react-i18next";
import type { TFunction } from "i18next";
import type { DayAvailability } from "@beauty/booking-core";
import { uk } from "@/i18n/uk";
import { buildMonthGrid, type CalendarCell, monthBounds, shiftMonth } from "@/lib/calendar";
import { longDate } from "@/lib/dates";
import { colors, fonts, radius, space, TOUCH } from "@/theme";

const WEEK_HEAD = [1, 2, 3, 4, 5, 6, 0].map((i) => uk.weekdaysShort[i]); // пн..нд
const TINT = "#EBDDE7";

/** Місячна сітка (пн-нд) зі станами доступності; навігація місяців у межах завантажених даних. */
export function MonthCalendar({
  month,
  days,
  selected,
  onMonthChange,
  onSelect,
}: {
  month: string; // YYYY-MM
  days: DayAvailability[];
  selected: string | null;
  onMonthChange: (ym: string) => void;
  onSelect: (iso: string) => void;
}) {
  const { t } = useTranslation();
  const bounds = monthBounds(days);
  const canPrev = !!bounds && month > bounds.first;
  const canNext = !!bounds && month < bounds.last;
  const [y, m] = month.split("-").map(Number);
  const weeks = buildMonthGrid(month, days);

  return (
    <View style={s.wrap}>
      <View style={s.nav}>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("time.prevMonth")}
          accessibilityState={{ disabled: !canPrev }}
          disabled={!canPrev}
          onPress={() => onMonthChange(shiftMonth(month, -1))}
          style={[s.navBtn, !canPrev && { opacity: 0.3 }]}
        >
          <Text style={s.navText}>‹</Text>
        </Pressable>
        <Text accessibilityRole="header" style={s.title}>{`${uk.monthsNominative[m - 1]} ${y}`}</Text>
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={t("time.nextMonth")}
          accessibilityState={{ disabled: !canNext }}
          disabled={!canNext}
          onPress={() => onMonthChange(shiftMonth(month, 1))}
          style={[s.navBtn, !canNext && { opacity: 0.3 }]}
        >
          <Text style={s.navText}>›</Text>
        </Pressable>
      </View>
      <View style={s.row}>
        {WEEK_HEAD.map((w) => (
          <Text key={w} style={s.head}>
            {w}
          </Text>
        ))}
      </View>
      {weeks.map((week, i) => (
        <View key={i} style={s.row}>
          {week.map((c) => (
            <Cell key={c.iso} cell={c} selected={selected === c.iso} onSelect={onSelect} />
          ))}
        </View>
      ))}
      <View style={s.legend}>
        <LegendItem style={s.cAvail} label={t("time.legendFree")} />
        <LegendItem style={s.cFull} label={t("time.legendFull")} strike />
        <LegendItem style={s.cClosed} label={t("time.legendClosed")} />
      </View>
    </View>
  );
}

function a11yText(c: CalendarCell, t: TFunction): string {
  const date = longDate(c.iso);
  switch (c.state) {
    case "available":
      return t("time.a11yAvailable", { date });
    case "full":
      return t("time.a11yFull", { date });
    case "closed":
      return t("time.a11yClosed", { date });
    case "past":
      return t("time.a11yPast", { date });
    default:
      return t("time.a11yOutside", { date });
  }
}

function Cell({ cell, selected, onSelect }: { cell: CalendarCell; selected: boolean; onSelect: (iso: string) => void }) {
  const { t } = useTranslation();
  if (!cell.inMonth) return <View style={s.cellBox} />;
  const st = cell.state;
  const on = selected && cell.selectable;
  return (
    <View style={s.cellBox}>
      <Pressable
        accessibilityRole="button"
        accessibilityLabel={a11yText(cell, t)}
        accessibilityState={{ disabled: !cell.selectable, selected: on }}
        disabled={!cell.selectable}
        onPress={() => onSelect(cell.iso)}
        style={[s.cell, st === "available" && s.cAvail, on && s.cOn, st === "full" && s.cFull, st === "closed" && s.cClosed]}
      >
        <Text
          style={[
            s.num,
            st === "available" && { color: colors.primary, fontFamily: fonts.bodyMedium },
            on && { color: colors.onPrimary },
            st === "full" && { color: colors.muted, textDecorationLine: "line-through" },
            st === "closed" && { color: colors.muted },
            (st === "past" || st === "outside") && { color: colors.border },
          ]}
        >
          {cell.day}
        </Text>
        {st === "available" ? <View style={[s.dot, on && { backgroundColor: colors.onPrimary }]} /> : null}
      </Pressable>
    </View>
  );
}

function LegendItem({ style, label, strike }: { style: object; label: string; strike?: boolean }) {
  return (
    <View style={s.legendItem}>
      <View style={[s.swatch, style]}>
        {strike ? <View style={s.swatchStrike} /> : null}
      </View>
      <Text style={s.legendText}>{label}</Text>
    </View>
  );
}

const s = StyleSheet.create({
  wrap: { backgroundColor: colors.surface, borderRadius: radius.lg, borderWidth: 1, borderColor: colors.border, padding: space.sm, gap: space.xs },
  nav: { flexDirection: "row", alignItems: "center", justifyContent: "space-between" },
  navBtn: { width: TOUCH, height: TOUCH, alignItems: "center", justifyContent: "center" },
  navText: { fontSize: 28, color: colors.primary, fontFamily: fonts.bodyMedium },
  title: { fontFamily: fonts.bodyMedium, fontSize: 16, color: colors.text },
  row: { flexDirection: "row" },
  head: { flex: 1, textAlign: "center", fontFamily: fonts.bodyMedium, fontSize: 12, color: colors.muted, paddingVertical: space.xs },
  cellBox: { flex: 1, minHeight: TOUCH, alignItems: "center", justifyContent: "center", padding: 1 },
  cell: { width: "100%", minHeight: TOUCH - 2, borderRadius: radius.md, alignItems: "center", justifyContent: "center" },
  cAvail: { backgroundColor: TINT, borderWidth: 1, borderColor: colors.primary },
  cOn: { backgroundColor: colors.primary },
  cFull: { backgroundColor: colors.bg },
  cClosed: { backgroundColor: "transparent" },
  num: { fontFamily: fonts.body, fontSize: 15, color: colors.text },
  dot: { width: 5, height: 5, borderRadius: 3, backgroundColor: colors.primary, marginTop: 1 },
  legend: { flexDirection: "row", flexWrap: "wrap", gap: space.lg, paddingTop: space.sm, paddingHorizontal: space.xs },
  legendItem: { flexDirection: "row", alignItems: "center", gap: space.xs },
  swatch: { width: 18, height: 18, borderRadius: 5, alignItems: "center", justifyContent: "center", borderWidth: 1, borderColor: colors.border },
  swatchStrike: { width: 12, height: 1, backgroundColor: colors.muted },
  legendText: { fontFamily: fonts.body, fontSize: 12, color: colors.muted },
});
