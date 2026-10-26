import { BeautyApiError } from "@/features/beauty-auth/errors";
import { CATALOG, blocksOn } from "../../beauty-shared/demo-seed";
import { dayAvailability } from "../../beauty-shared/availability";
import { addDaysYmd, mondayOfYmd } from "../../beauty-shared/zone-time";
import { previousRange, referenceZone, validateRange, type DateRange } from "../analytics/ranges";
import type {
  Absence,
  Appointment,
  AnalyticsQuery,
  BeautyLocation,
  LocationBreakdown,
  LocationMetrics,
  LocationStat,
  NetworkAnalytics,
  NetworkSeries,
  NetworkTotals,
  PromotionStat,
  SeriesGranularity,
  SeriesPoint,
  SpecialistStat,
  StaffMember,
  LocationClosure,
} from "../types";

/**
 * Mock аналітики за контрактом §19: датний режим, series, compare/previous, load, breakdown, 422 invalid_range.
 * Рахується з ТИХ САМИХ записів, графіків і годин, що календар/огляд (єдиний demo-seed, TASK-705): окремих «магічних»
 * цифр немає. Запис належить локальному дню свого закладу (`startsAt` у mock уже локальний час закладу).
 */

export interface AnalyticsCtx {
  /** Історія + поточні/майбутні записи (з урахуванням скасувань mock). */
  appointments: Appointment[];
  locations: BeautyLocation[];
  staff: StaffMember[];
  absences: Pick<Absence, "specialistId" | "dateFrom" | "dateTo" | "status">[];
  closures: LocationClosure[];
  /** Сьогодні в еталонній зоні (`yyyy-MM-dd`). */
  today: string;
}

/** Коли клієнт «зареєструвався» (днів тому): джерело `newClients`; записи клієнта до цієї дати в аналітику не входять. */
const CLIENT_JOINED_DAYS_AGO: Record<string, number> = { oc: 520, tg: 410, kl: 600, sm: 45, mp: 60 };
const joinedOn = (clientId: string | null, today: string) => (clientId && clientId in CLIENT_JOINED_DAYS_AGO ? addDaysYmd(today, -CLIENT_JOINED_DAYS_AGO[clientId]) : "0000-00-00");

const dateOf = (a: Appointment) => a.startsAt.slice(0, 10);
const inRange = (d: string, r: DateRange) => d >= r.from && d <= r.to;
const BUSY = new Set(["pending", "confirmed", "in_progress", "completed"]);

export function assertRange(q: Pick<AnalyticsQuery, "from" | "to">): DateRange {
  if (validateRange(q.from, q.to)) throw new BeautyApiError(422, "invalid_range", "invalid range");
  return { from: q.from, to: q.to };
}

function eachDay(r: DateRange): string[] {
  const out: string[] = [];
  for (let d = r.from; d <= r.to; d = addDaysYmd(d, 1)) out.push(d);
  return out;
}

export function granularityFor(days: number): SeriesGranularity {
  return days <= 31 ? "day" : days <= 120 ? "week" : "month";
}

/** Кошики series (§19.2): дні / ISO-тижні пн–нд / календарні місяці, перший і останній обрізані межами. */
export function bucketsFor(r: DateRange, g: SeriesGranularity): DateRange[] {
  const out: DateRange[] = [];
  let start = r.from;
  while (start <= r.to) {
    let end: string;
    if (g === "day") end = start;
    else if (g === "week") end = addDaysYmd(mondayOfYmd(start), 6);
    else {
      const y = +start.slice(0, 4);
      const m = +start.slice(5, 7);
      end = addDaysYmd(`${m === 12 ? y + 1 : y}-${String(m === 12 ? 1 : m + 1).padStart(2, "0")}-01`, -1);
    }
    if (end > r.to) end = r.to;
    out.push({ from: start, to: end });
    start = addDaysYmd(end, 1);
  }
  return out;
}

export function createMockAnalytics(ctx: AnalyticsCtx) {
  const all = ctx.appointments.filter((a) => dateOf(a) >= joinedOn(a.clientId, ctx.today));
  const zone = referenceZone(ctx.locations.map((l) => ({ id: l.id, name: l.name, timezone: l.timezone, isActive: l.isActive })));

  const within = (r: DateRange, pred: (a: Appointment) => boolean = () => true) => all.filter((a) => inRange(dateOf(a), r) && pred(a));

  function counts(list: Appointment[]) {
    const completed = list.filter((a) => a.status === "completed");
    return {
      appointments: list.length,
      completed: completed.length,
      cancelled: list.filter((a) => a.status === "cancelled").length,
      noShow: list.filter((a) => a.status === "no_show").length,
      revenue: completed.reduce((s, a) => s + a.priceFinal, 0),
    };
  }

  function totals(r: DateRange): NetworkTotals {
    const c = counts(within(r));
    const newClients = Object.keys(CLIENT_JOINED_DAYS_AGO).filter((id) => inRange(joinedOn(id, ctx.today), r)).length;
    return {
      ...c,
      averageCheck: c.completed ? Math.round(c.revenue / c.completed) : 0,
      cancellationRate: c.appointments ? c.cancelled / c.appointments : 0,
      newClients,
    };
  }

  /** Доступні хвилини майстра в закладі (§19.4): графік ∩ години ∩ вихідні/закриття; approved-відсутність = 0; майбутнє неактивного = 0. */
  function availableMinutes(m: StaffMember, loc: BeautyLocation, r: DateRange): number {
    const link = m.locations.find((l) => l.locationId === loc.id);
    if (!link) return 0;
    const rules = {
      masterHours: link.workingHours,
      locationHours: loc.workingHours,
      closedWeekdays: loc.closedWeekdays,
      closures: ctx.closures.filter((c) => c.locationId === loc.id),
    };
    let sum = 0;
    for (const d of eachDay(r)) {
      if (d > ctx.today && (!m.isActive || link.isActive === false)) continue;
      sum += dayAvailability(d, rules, blocksOn(ctx.absences, m.id, d)).free.reduce((s, i) => s + (i.to - i.from), 0);
    }
    return sum;
  }

  const loadOf = (busy: number, avail: number) => (avail === 0 ? null : Math.round(Math.min(busy / avail, 1) * 10000) / 10000);
  const busyOf = (list: Appointment[]) => list.filter((a) => BUSY.has(a.status)).reduce((s, a) => s + a.durationMinutes, 0);

  /** Майстри закладу: із записами в діапазоні або зі зв'язком і доступними хвилинами. */
  function specialistRows(loc: BeautyLocation, r: DateRange): SpecialistStat[] {
    const rows: SpecialistStat[] = [];
    for (const m of ctx.staff) {
      const mine = within(r, (a) => a.locationId === loc.id && a.specialistId === m.id);
      const avail = availableMinutes(m, loc, r);
      if (mine.length === 0 && avail === 0) continue;
      const busy = busyOf(mine);
      rows.push({ specialistId: m.id, name: m.name, isActive: m.isActive, ...counts(mine), busyMinutes: busy, availableMinutes: avail, load: loadOf(busy, avail) });
    }
    return rows.sort((a, b) => b.revenue - a.revenue || a.name.localeCompare(b.name, "uk"));
  }

  function locationMetrics(loc: BeautyLocation, r: DateRange): LocationMetrics {
    const rows = specialistRows(loc, r);
    // Записи без «знайденого» майстра (напр. видалений) теж рахуються у закладі.
    const c = counts(within(r, (a) => a.locationId === loc.id));
    const busy = rows.reduce((s, x) => s + x.busyMinutes, 0);
    const avail = rows.reduce((s, x) => s + x.availableMinutes, 0);
    return { ...c, busyMinutes: busy, availableMinutes: avail, load: loadOf(busy, avail) };
  }

  const hasData = (loc: BeautyLocation, r: DateRange) => within(r, (a) => a.locationId === loc.id).length > 0;

  return {
    zone,

    network(q: AnalyticsQuery): NetworkAnalytics {
      const r = assertRange(q);
      const p = previousRange(r);
      return {
        fromDate: r.from,
        toDate: r.to,
        timezone: zone,
        totals: totals(r),
        previous: q.compare ? { ...totals(p), fromDate: p.from, toDate: p.to } : null,
      };
    },

    series(q: Pick<AnalyticsQuery, "from" | "to">): NetworkSeries {
      const r = assertRange(q);
      const granularity = granularityFor(eachDay(r).length);
      const points: SeriesPoint[] = bucketsFor(r, granularity).map((b) => ({ from: b.from, to: b.to, ...counts(within(b)) }));
      return { fromDate: r.from, toDate: r.to, timezone: zone, granularity, points };
    },

    locations(q: AnalyticsQuery): LocationStat[] {
      const r = assertRange(q);
      const p = previousRange(r);
      return ctx.locations
        .filter((l) => l.isActive !== false || hasData(l, r) || (q.compare && hasData(l, p)))
        .map<LocationStat>((l) => ({
          locationId: l.id,
          name: l.name,
          timezone: l.timezone ?? "UTC",
          isActive: l.isActive !== false,
          ...locationMetrics(l, r),
          previous: q.compare ? locationMetrics(l, p) : null,
        }))
        .sort((a, b) => b.revenue - a.revenue || a.name.localeCompare(b.name, "uk"));
    },

    breakdown(locationId: string, q: Pick<AnalyticsQuery, "from" | "to">): LocationBreakdown {
      const r = assertRange(q);
      const loc = ctx.locations.find((l) => l.id === locationId);
      if (!loc) throw new BeautyApiError(404, "location_not_found", "location not found");
      const specialists = specialistRows(loc, r);
      const busy = specialists.reduce((s, x) => s + x.busyMinutes, 0);
      const avail = specialists.reduce((s, x) => s + x.availableMinutes, 0);
      const byService = new Map<string, { name: string; completed: number; revenue: number }>();
      for (const a of within(r, (x) => x.locationId === loc.id && x.status === "completed")) {
        const e = byService.get(a.serviceId) ?? { name: a.serviceName, completed: 0, revenue: 0 };
        e.completed += 1;
        e.revenue += a.priceFinal;
        byService.set(a.serviceId, e);
      }
      return {
        locationId: loc.id,
        name: loc.name,
        timezone: loc.timezone ?? "UTC",
        fromDate: r.from,
        toDate: r.to,
        busyMinutes: busy,
        availableMinutes: avail,
        load: loadOf(busy, avail),
        specialists,
        services: [...byService.entries()]
          .map(([serviceId, v]) => ({ serviceId, ...v }))
          .sort((a, b) => b.revenue - a.revenue)
          .slice(0, 50),
      };
    },

    promotions(q: AnalyticsQuery): PromotionStat[] {
      const r = assertRange(q);
      const p = previousRange(r);
      const stats = (range: DateRange) => {
        const m = new Map<string, { uses: number; revenue: number; discountTotal: number; locations: Set<string> }>();
        for (const a of within(range, (x) => !!x.promotionName && x.status !== "cancelled" && x.status !== "no_show")) {
          const e = m.get(a.promotionName!) ?? { uses: 0, revenue: 0, discountTotal: 0, locations: new Set<string>() };
          e.uses += 1;
          e.locations.add(a.locationId);
          if (a.status === "completed") {
            e.revenue += a.priceFinal;
            e.discountTotal += Math.max(0, (CATALOG.find((s) => s.id === a.serviceId)?.price ?? a.priceFinal) - a.priceFinal);
          }
          m.set(a.promotionName!, e);
        }
        return m;
      };
      const cur = stats(r);
      const prev = q.compare ? stats(p) : new Map<string, never>();
      const names = new Set([...cur.keys(), ...prev.keys()]);
      return [...names]
        .map<PromotionStat>((name) => {
          const c = cur.get(name) ?? { uses: 0, revenue: 0, discountTotal: 0, locations: new Set<string>() };
          const pv = prev.get(name) as { uses: number; revenue: number; discountTotal: number } | undefined;
          const locNames = [...c.locations].map((id) => ctx.locations.find((l) => l.id === id)?.name ?? id);
          return {
            id: `promo-${name}`,
            name,
            period: "постійна",
            where: locNames.length ? locNames.join(", ") : "Усі заклади",
            uses: c.uses,
            revenue: c.revenue,
            discountTotal: c.discountTotal,
            previous: q.compare ? { uses: pv?.uses ?? 0, revenue: pv?.revenue ?? 0, discountTotal: pv?.discountTotal ?? 0 } : null,
          };
        })
        .sort((a, b) => b.uses - a.uses || a.name.localeCompare(b.name, "uk"));
    },
  };
}
