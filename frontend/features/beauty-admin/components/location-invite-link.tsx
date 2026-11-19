"use client";

import { QRCodeCanvas, QRCodeSVG } from "qrcode.react";
import { useId, useRef, useState } from "react";
import { formatPublicCode, normalizePublicCode } from "@/lib/applinks/location-code";
import type { BeautyLocation } from "../types";
import { Card, PrimaryButton, SecondaryButton } from "./ui";

/** Базовий origin: NEXT_PUBLIC_SITE_URL, інакше поточний origin вікна (як для посилань-запрошень персоналу). */
export function siteOrigin(): string {
  const env = process.env.NEXT_PUBLIC_SITE_URL?.trim().replace(/\/+$/, "");
  if (env) return env;
  return typeof window === "undefined" ? "" : window.location.origin;
}

/** Повне посилання для клієнтів: `{origin}/l/{code}` (код у нормалізованому вигляді без дефіса). */
export const inviteLinkFor = (origin: string, code: string): string => `${origin}/l/${code}`;

function download(href: string, filename: string) {
  const a = document.createElement("a");
  a.href = href;
  a.download = filename;
  document.body.appendChild(a);
  a.click();
  a.remove();
}

/**
 * Блок «Посилання для клієнтів» (ADR-022): публічний код, посилання, копіювання, QR (генерується локально,
 * URL нікому не передається). Без валідного publicCode не показується.
 */
export function LocationInviteLink({ location }: { location: BeautyLocation }) {
  const code = normalizePublicCode(location.publicCode);
  const [origin] = useState(siteOrigin);
  const [copied, setCopied] = useState<"ok" | "fail" | null>(null);
  const linkId = useId();
  const svgWrap = useRef<HTMLDivElement>(null);
  const canvasWrap = useRef<HTMLDivElement>(null);
  if (!code) return null;

  const link = inviteLinkFor(origin, code);
  const label = formatPublicCode(code);

  async function copy() {
    try {
      await navigator.clipboard.writeText(link);
      setCopied("ok");
    } catch {
      setCopied("fail");
    }
  }

  function downloadSvg() {
    const svg = svgWrap.current?.querySelector("svg");
    if (!svg) return;
    const xml = new XMLSerializer().serializeToString(svg);
    const url = URL.createObjectURL(new Blob([xml], { type: "image/svg+xml" }));
    download(url, `qr-${code}.svg`);
    URL.revokeObjectURL(url);
  }

  function downloadPng() {
    const canvas = canvasWrap.current?.querySelector("canvas");
    if (!canvas) return;
    download(canvas.toDataURL("image/png"), `qr-${code}.png`);
  }

  return (
    <Card className="flex min-w-0 flex-col gap-4">
      <h2 className="text-lg font-semibold">Посилання для клієнтів</h2>
      <p className="text-sm text-(--muted)">
        Поділіться кодом, посиланням або QR: клієнт відкриє запис саме до цього закладу. Код постійний і не змінюється.
      </p>
      <dl className="m-0 flex flex-col gap-1.5 text-sm">
        <div className="flex gap-2">
          <dt className="w-28 flex-none text-(--muted)">Код закладу</dt>
          <dd className="m-0 font-mono text-base font-semibold tracking-wider" data-testid="invite-code">
            {label}
          </dd>
        </div>
      </dl>
      <div className="flex flex-col gap-1.5">
        <label htmlFor={linkId} className="text-sm font-semibold">
          Посилання
        </label>
        <input
          id={linkId}
          readOnly
          value={link}
          onFocus={(e) => e.currentTarget.select()}
          className="min-h-11 w-full min-w-0 rounded-xl border border-(--line-strong) bg-white px-3 font-mono text-[13px]"
        />
      </div>
      <div ref={svgWrap} className="w-fit rounded-xl border border-(--line) bg-white p-2">
        <QRCodeSVG value={link} size={160} level="M" marginSize={2} role="img" title={`QR-код посилання на заклад ${location.name}`} />
      </div>
      <div ref={canvasWrap} hidden aria-hidden="true">
        <QRCodeCanvas value={link} size={640} level="M" marginSize={4} />
      </div>
      <div className="flex flex-wrap gap-2">
        <PrimaryButton onClick={() => void copy()}>Копіювати</PrimaryButton>
        <SecondaryButton onClick={downloadPng}>Завантажити QR (PNG)</SecondaryButton>
        <SecondaryButton onClick={downloadSvg}>Завантажити QR (SVG)</SecondaryButton>
      </div>
      <p role="status" aria-live="polite" className={`min-h-5 text-sm font-semibold ${copied === "fail" ? "text-[#8A1F1F]" : "text-[#145A32]"}`}>
        {copied === "ok" ? "Посилання скопійовано." : null}
        {copied === "fail" ? "Не вдалося скопіювати автоматично. Виділіть посилання в полі й скопіюйте вручну." : null}
      </p>
    </Card>
  );
}
