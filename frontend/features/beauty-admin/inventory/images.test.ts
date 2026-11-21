import { describe, expect, it } from "vitest";
import { BeautyApiError } from "@/features/beauty-auth/errors";
import { humanizeImageError, moveId, validateImageFile } from "./images";

describe("validateImageFile", () => {
  it("приймає jpeg/png/webp до 10 МБ, відхиляє інше", () => {
    expect(validateImageFile({ type: "image/webp", size: 1000 }, 0)).toBeNull();
    expect(validateImageFile({ type: "image/gif", size: 1000 }, 0)).toMatch(/JPEG, PNG або WebP/);
    expect(validateImageFile({ type: "image/png", size: 10 * 1024 * 1024 + 1 }, 0)).toMatch(/10 МБ/);
    expect(validateImageFile({ type: "image/png", size: 1000 }, 8)).toMatch(/Не більше 8/);
  });
});

describe("moveId", () => {
  it("міняє сусідів і не виходить за межі", () => {
    expect(moveId(["a", "b", "c"], 1, -1)).toEqual(["b", "a", "c"]);
    expect(moveId(["a", "b", "c"], 1, 1)).toEqual(["a", "c", "b"]);
    expect(moveId(["a", "b"], 0, -1)).toEqual(["a", "b"]);
    expect(moveId(["a", "b"], 1, 1)).toEqual(["a", "b"]);
  });
});

describe("humanizeImageError", () => {
  it("коди й статуси без коду (nginx 413) -> українські тексти", () => {
    expect(humanizeImageError(new BeautyApiError(404, "product_images_disabled", "x"))).toMatch(/не підключено/);
    expect(humanizeImageError(new BeautyApiError(422, "invalid_image_order", "x"))).toMatch(/Порядок/);
    expect(humanizeImageError(new BeautyApiError(413, "", "Request Entity Too Large"))).toMatch(/10 МБ/);
    expect(humanizeImageError(new BeautyApiError(503, "media_busy", "x"))).toMatch(/кілька секунд/);
    expect(humanizeImageError(new BeautyApiError(429, "rate_limited", "x"))).toMatch(/Забагато/);
  });
});
