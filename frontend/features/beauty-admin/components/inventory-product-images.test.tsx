import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { DEMO_LOCATIONS } from "../../beauty-shared/demo-seed";
import { createMockInventory } from "../api/mock-inventory";

const state = vi.hoisted(() => ({ api: null as unknown }));
vi.mock("../api", () => ({ beautyApi: new Proxy({}, { get: (_t, k) => (state.api as Record<string | symbol, unknown>)[k] }) }));

import { ProductImages } from "./inventory-product-images";

type Inv = ReturnType<typeof createMockInventory>;
let inv: Inv;
let productId: string;

const png = (name: string) => new File(["x"], name, { type: "image/png" });

function renderGallery(canEdit = true) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <ProductImages productId={productId} productName="Шампунь" canEdit={canEdit} />
    </QueryClientProvider>,
  );
}

beforeEach(async () => {
  inv = createMockInventory({ role: () => "owner", locations: () => DEMO_LOCATIONS, now: () => Date.now(), wait: false });
  state.api = inv;
  productId = (await inv.getInventoryProducts({ active: "all" })).items[0]!.id;
});
afterEach(() => cleanup());

describe("галерея фото товару (§33.22)", () => {
  it("завантаження, порядок з кнопок, видалення з підтвердженням", async () => {
    const user = userEvent.setup();
    renderGallery();
    expect(await screen.findByText("Фото ще немає.")).toBeInTheDocument();

    const input = screen.getByTestId("product-image-input");
    await user.upload(input, png("a.png"));
    await user.upload(input, png("b.png"));
    expect(await screen.findByAltText(/Фото 2 з 2 товару «Шампунь»/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Фото 1: перемістити вгору" })).toBeDisabled();

    const first = (await inv.getInventoryProduct(productId)).images[0]!.id;
    await user.click(screen.getByRole("button", { name: "Фото 2: перемістити вгору" }));
    await waitFor(async () => expect((await inv.getInventoryProduct(productId)).images[1]!.id).toBe(first));
    expect(await screen.findByText(/переміщено на позицію 1 з 2/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Видалити фото 1" }));
    expect(screen.getByText(/Дію не можна скасувати/)).toHaveFocus();
    await user.click(screen.getByRole("button", { name: "Так, видалити" }));
    expect(await screen.findByText("Фото видалено.")).toBeInTheDocument();
    expect((await inv.getInventoryProduct(productId)).images).toHaveLength(1);
  });

  it("клієнтська перевірка типу і зрозуміла помилка", async () => {
    const user = userEvent.setup({ applyAccept: false });
    renderGallery();
    await screen.findByText("Фото ще немає.");
    await user.upload(screen.getByTestId("product-image-input"), new File(["x"], "a.gif", { type: "image/gif" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/JPEG, PNG або WebP/);
  });

  it("без права canEditCatalog дій немає, мініатюри видно", async () => {
    await inv.addProductImage(productId, png("a.png"));
    renderGallery(false);
    expect(await screen.findByAltText(/Фото 1 з 1/)).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
    expect(screen.queryByTestId("product-image-input")).toBeNull();
  });
});
