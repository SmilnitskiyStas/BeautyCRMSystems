import { createLocalStore, type PlainKV, type SecureKV } from "../tokenStore";

function memory() {
  const secureMap = new Map<string, string>();
  const plainMap = new Map<string, string>();
  const secure: SecureKV = {
    getItemAsync: async (k) => secureMap.get(k) ?? null,
    setItemAsync: async (k, v) => {
      // SecureStore: ключ лише [A-Za-z0-9._-]
      if (!/^[A-Za-z0-9._-]+$/.test(k)) throw new Error(`invalid secure key ${k}`);
      secureMap.set(k, v);
    },
    deleteItemAsync: async (k) => void secureMap.delete(k),
  };
  const plain: PlainKV = {
    getItem: async (k) => plainMap.get(k) ?? null,
    setItem: async (k, v) => void plainMap.set(k, v),
    removeItem: async (k) => void plainMap.delete(k),
  };
  return { secure, plain, secureMap, plainMap };
}

const rec = (id: string, startsAt = "2026-11-12T14:30:00+02:00") => ({
  localId: id,
  tenantSlug: "salon-a",
  locationName: "Salon",
  serviceName: "Manicure",
  specialistName: "Anna",
  startsAt,
  createdAt: "2026-11-09T10:00:00.000Z",
});

describe("local store", () => {
  it("stores each token under its own SecureStore key and keeps tokens out of the index", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.saveAppointment(rec("11111111-aaaa"), "TOKEN-ONE");
    await s.saveAppointment(rec("22222222-bbbb"), "TOKEN-TWO");

    expect(m.secureMap.get("appt-token.11111111-aaaa")).toBe("TOKEN-ONE");
    expect(m.secureMap.get("appt-token.22222222-bbbb")).toBe("TOKEN-TWO");
    expect(await s.getToken("22222222-bbbb")).toBe("TOKEN-TWO");

    const indexRaw = [...m.plainMap.values()].join("");
    expect(indexRaw).not.toContain("TOKEN-ONE");
    expect(indexRaw).not.toContain("TOKEN-TWO");
    const list = await s.listAppointments();
    expect(list).toHaveLength(2);
    expect(JSON.stringify(list)).not.toContain("TOKEN");
  });

  it("upserts by localId (idempotent replay does not duplicate)", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.saveAppointment(rec("11111111-aaaa"), "T");
    await s.saveAppointment({ ...rec("11111111-aaaa"), status: "confirmed" }, "T");
    const list = await s.listAppointments();
    expect(list).toHaveLength(1);
    expect(list[0].status).toBe("confirmed");
  });

  it("rejects path-like localId for token reads", async () => {
    const s = createLocalStore(memory().secure, memory().plain);
    expect(await s.getToken("../../x")).toBeNull();
  });

  it("removes one appointment with its token", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.saveAppointment(rec("11111111-aaaa"), "T1");
    await s.saveAppointment(rec("22222222-bbbb"), "T2");
    await s.removeAppointment("11111111-aaaa");
    expect(await s.getToken("11111111-aaaa")).toBeNull();
    expect((await s.listAppointments()).map((r) => r.localId)).toEqual(["22222222-bbbb"]);
  });

  it("survives a corrupted index (bad entries dropped, bad JSON -> empty)", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.saveAppointment(rec("11111111-aaaa"), "T1");
    const key = [...m.plainMap.keys()][0];
    m.plainMap.set(key, JSON.stringify([rec("11111111-aaaa"), { junk: true }]));
    expect(await s.listAppointments()).toHaveLength(1);
    m.plainMap.set(key, "{not json");
    expect(await s.listAppointments()).toEqual([]);
  });

  it("wipeAll removes tokens, profile, index and last location - even if the index is corrupted", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.saveAppointment(rec("11111111-aaaa"), "T1");
    await s.saveAppointment(rec("22222222-bbbb"), "T2");
    await s.setProfile({ name: "Olena", phone: "+380501112233" });
    await s.setLastLocation({ publicCode: "7K2M9QXA", name: "Salon" });
    // пошкоджуємо індекс: токени мають видалитись через резервний реєстр
    m.plainMap.set([...m.plainMap.keys()].find((k) => k.includes("index"))!, "garbage");

    await s.wipeAll();

    expect(m.secureMap.size).toBe(0);
    expect(m.plainMap.size).toBe(0);
    expect(await s.getProfile()).toBeNull();
    expect(await s.getLastLocation()).toBeNull();
  });

  it("stores only code and name as the last location (no coordinates)", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.setLastLocation({ publicCode: "7K2M9QXA", name: "Salon", lat: 50.45, lng: 30.52 } as never);
    const raw = [...m.plainMap.values()][0];
    expect(raw).not.toMatch(/lat|lng|50\.45/);
    expect(await s.getLastLocation()).toEqual({ publicCode: "7K2M9QXA", name: "Salon" });
  });
});

describe("first run cleanup (stale Keychain after reinstall)", () => {
  it("purges leftover SecureStore data when AsyncStorage is empty, then sets the flag", async () => {
    const m = memory();
    m.secureMap.set("appt-token.11111111-aaaa", "OLD");
    m.secureMap.set("appt-token-ids", JSON.stringify(["11111111-aaaa"]));
    m.secureMap.set("beauty.profile.v1", JSON.stringify({ name: "A", phone: "1" }));
    const s = createLocalStore(m.secure, m.plain);
    expect(await s.firstRunCleanup()).toBe("purged");
    expect(m.secureMap.size).toBe(0);
    expect(await s.firstRunCleanup()).toBe("skipped");
  });

  it("keeps data of an existing install (index present) and only sets the flag", async () => {
    const m = memory();
    const s = createLocalStore(m.secure, m.plain);
    await s.saveAppointment(rec("11111111-aaaa"), "TOKEN");
    m.plainMap.delete("beauty.firstRunDone.v1");
    expect(await s.firstRunCleanup()).toBe("kept");
    expect(await s.getToken("11111111-aaaa")).toBe("TOKEN");
  });
});
