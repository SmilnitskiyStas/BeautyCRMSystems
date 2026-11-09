import { withUpgradeGuard } from "../upgrade";

describe("withUpgradeGuard", () => {
  it("flags 426 and still returns the response", async () => {
    const onUpgrade = jest.fn();
    const res = { status: 426 };
    const f = withUpgradeGuard(async (_u: string) => res, onUpgrade);
    await expect(f("x")).resolves.toBe(res);
    expect(onUpgrade).toHaveBeenCalledTimes(1);
  });
  it("ignores other statuses", async () => {
    const onUpgrade = jest.fn();
    await withUpgradeGuard(async (_u: string) => ({ status: 409 }), onUpgrade)("x");
    await withUpgradeGuard(async (_u: string) => ({ status: 200 }), onUpgrade)("x");
    expect(onUpgrade).not.toHaveBeenCalled();
  });
});
