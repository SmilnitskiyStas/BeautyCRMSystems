import { describe, expect, it } from "vitest";
import { mastersForCalendar, type RosterEntry } from "./calendar-masters";

const roster: RosterEntry[] = [
  { id: "a", name: "Активна", isActive: true, locationNames: ["Центр"] },
  { id: "i1", name: "Неактивна із записами", isActive: false, locationNames: ["Поділ", "Центр"] },
  { id: "i2", name: "Неактивна без записів", isActive: false, locationNames: ["Центр"] },
  { id: "i3", name: "Неактивна лише скасовані", isActive: false, locationNames: ["Центр"] },
];

describe("mastersForCalendar (§16)", () => {
  const appts = [
    { specialistId: "i1", status: "confirmed" as const },
    { specialistId: "i1", status: "pending" as const },
    { specialistId: "i1", status: "completed" as const },
    { specialistId: "i3", status: "cancelled" as const },
  ];

  it("неактивні без нескасованих записів не потрапляють у селектор", () => {
    expect(mastersForCalendar(roster, appts).map((m) => m.id)).toEqual(["a", "i1"]);
  });

  it("неактивний із записами має позначку й кількість записів для перенесення (лише pending/confirmed)", () => {
    const m = mastersForCalendar(roster, appts).find((x) => x.id === "i1");
    expect(m).toMatchObject({ isActive: false, toMoveCount: 2, locationName: "Поділ, Центр" });
  });

  it("активний майстер завжди в списку з toMoveCount = 0", () => {
    expect(mastersForCalendar(roster, [])[0]).toMatchObject({ id: "a", isActive: true, toMoveCount: 0 });
  });

  it("specialist бачить лише себе", () => {
    expect(mastersForCalendar(roster, appts, "i1").map((m) => m.id)).toEqual(["i1"]);
    expect(mastersForCalendar(roster, appts, "i2")).toEqual([]);
  });
});
