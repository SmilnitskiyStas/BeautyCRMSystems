import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import type { BeautyLocation } from "../types";

const uploadLocationLogo = vi.fn<(id: string, f: File) => Promise<BeautyLocation>>();
const deleteLocationLogo = vi.fn<(id: string) => Promise<BeautyLocation>>();
vi.mock("../api", () => ({
  beautyApi: { uploadLocationLogo: (id: string, f: File) => uploadLocationLogo(id, f), deleteLocationLogo: (id: string) => deleteLocationLogo(id) },
}));

import { LocationAvatar, LocationLogoBlock, locationInitials } from "./location-logo";

const base: BeautyLocation = { id: "c", name: "Beauty Центр", logoUrl: null };
const withLogo: BeautyLocation = { ...base, logoUrl: "https://cdn.example/p/abc-400.webp" };
const png = new File(["x"], "a.png", { type: "image/png" });

function renderBlock(location: BeautyLocation, canEdit = true) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return render(
    <QueryClientProvider client={qc}>
      <LocationLogoBlock location={location} canEdit={canEdit} />
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  uploadLocationLogo.mockReset().mockResolvedValue(withLogo);
  deleteLocationLogo.mockReset().mockResolvedValue(base);
});
afterEach(() => cleanup());

describe("логотип закладу", () => {
  it("без логотипу показує ініціали та кнопку завантаження", () => {
    renderBlock(base);
    expect(screen.getByRole("img", { name: /ініціали/ }).textContent).toBe("BЦ");
    expect(screen.getByRole("button", { name: "Завантажити логотип" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Видалити" })).not.toBeInTheDocument();
  });

  it("з логотипом показує зображення, «Замінити» і «Видалити»", () => {
    renderBlock(withLogo);
    expect(screen.getByAltText(/Логотип закладу «Beauty Центр»/)).toHaveAttribute("src", withLogo.logoUrl);
    expect(screen.getByRole("button", { name: "Замінити" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Видалити" })).toBeInTheDocument();
  });

  it("без права редагування кнопок немає", () => {
    renderBlock(withLogo, false);
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("завантаження викликає API з файлом", async () => {
    const user = userEvent.setup();
    renderBlock(base);
    await user.upload(screen.getByTestId("location-logo-input"), png);
    await waitFor(() => expect(uploadLocationLogo).toHaveBeenCalledWith("c", png));
    expect(await screen.findByText("Логотип завантажено.")).toBeInTheDocument();
  });

  it("видалення викликає API", async () => {
    const user = userEvent.setup();
    renderBlock(withLogo);
    await user.click(screen.getByRole("button", { name: "Видалити" }));
    await waitFor(() => expect(deleteLocationLogo).toHaveBeenCalledWith("c"));
    expect(await screen.findByText("Логотип видалено.")).toBeInTheDocument();
  });

  it("помилка API показується українською", async () => {
    uploadLocationLogo.mockRejectedValue(new BeautyApiError(415, "unsupported_image_type", "x"));
    const user = userEvent.setup();
    renderBlock(base);
    await user.upload(screen.getByTestId("location-logo-input"), png);
    expect(await screen.findByRole("alert")).toHaveTextContent(/JPEG, PNG або WebP/);
  });

  it("клієнтська перевірка типу не викликає API", async () => {
    const user = userEvent.setup({ applyAccept: false });
    renderBlock(base);
    await user.upload(screen.getByTestId("location-logo-input"), new File(["x"], "a.gif", { type: "image/gif" }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/JPEG, PNG або WebP/);
    expect(uploadLocationLogo).not.toHaveBeenCalled();
  });

  it("аватар у списку: ініціали або зображення", () => {
    expect(locationInitials("Салон")).toBe("СА");
    const { container } = render(<LocationAvatar location={withLogo} />);
    expect(container.querySelector("img")).toHaveAttribute("src", withLogo.logoUrl);
  });
});
