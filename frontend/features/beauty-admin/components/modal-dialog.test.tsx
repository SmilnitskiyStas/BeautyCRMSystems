import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { ModalDialog } from "./modal-dialog";

describe("ModalDialog (портал)", () => {
  it("рендериться всередині .beauty-admin, щоб діалог мав токени теми й видимий колір тексту", () => {
    render(
      <ModalDialog title="Надіслати клієнтам?" actions={<button type="button">Так</button>} onClose={vi.fn()}>
        <p>Умови знижки</p>
      </ModalDialog>,
    );
    const dialog = screen.getByRole("dialog");
    expect(dialog.closest(".beauty-admin")).not.toBeNull();
    expect(screen.getByText("Умови знижки")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Так" })).toBeTruthy();
  });
});
