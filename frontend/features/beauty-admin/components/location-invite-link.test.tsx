// @vitest-environment jsdom
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";
import type { BeautyLocation } from "../types";
import { inviteLinkFor, LocationInviteLink } from "./location-invite-link";

const loc = (over: Partial<BeautyLocation> = {}): BeautyLocation => ({ id: "l1", name: "Центр", publicCode: "7K3M9PQ2", ...over });

afterEach(() => vi.unstubAllEnvs());

describe("LocationInviteLink", () => {
  it("не показується без коду або з хибним форматом", () => {
    const { container, rerender } = render(<LocationInviteLink location={loc({ publicCode: null })} />);
    expect(container).toBeEmptyDOMElement();
    rerender(<LocationInviteLink location={loc({ publicCode: undefined })} />);
    expect(container).toBeEmptyDOMElement();
    rerender(<LocationInviteLink location={loc({ publicCode: "abc" })} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("показує код XXXX-XXXX, посилання та QR", () => {
    render(<LocationInviteLink location={loc()} />);
    expect(screen.getByRole("heading", { name: "Посилання для клієнтів" })).toBeInTheDocument();
    expect(screen.getByTestId("invite-code")).toHaveTextContent("7K3M-9PQ2");
    const input = screen.getByLabelText("Посилання") as HTMLInputElement;
    expect(input.value).toBe(`${window.location.origin}/l/7K3M9PQ2`);
    expect(screen.getByRole("img", { name: /QR-код/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Завантажити QR \(PNG\)/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Завантажити QR \(SVG\)/ })).toBeInTheDocument();
  });

  it("надає перевагу NEXT_PUBLIC_SITE_URL", () => {
    vi.stubEnv("NEXT_PUBLIC_SITE_URL", "https://beauty.example/");
    render(<LocationInviteLink location={loc()} />);
    expect((screen.getByLabelText("Посилання") as HTMLInputElement).value).toBe("https://beauty.example/l/7K3M9PQ2");
    expect(inviteLinkFor("https://x.y", "ABCD1234")).toBe("https://x.y/l/ABCD1234");
  });

  it("копіює посилання й оголошує результат у aria-live", async () => {
    const user = userEvent.setup();
    const writeText = vi.spyOn(navigator.clipboard, "writeText").mockResolvedValue();
    render(<LocationInviteLink location={loc()} />);
    await user.click(screen.getByRole("button", { name: "Копіювати" }));
    expect(writeText).toHaveBeenCalledWith(`${window.location.origin}/l/7K3M9PQ2`);
    const status = await screen.findByRole("status");
    expect(status).toHaveTextContent("Посилання скопійовано.");
    expect(status).toHaveAttribute("aria-live", "polite");
  });

  it("повідомляє, якщо копіювання недоступне", async () => {
    const user = userEvent.setup();
    vi.spyOn(navigator.clipboard, "writeText").mockRejectedValue(new Error("denied"));
    render(<LocationInviteLink location={loc()} />);
    await user.click(screen.getByRole("button", { name: "Копіювати" }));
    expect(await screen.findByRole("status")).toHaveTextContent("Не вдалося скопіювати");
  });
});
