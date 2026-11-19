"use client";

import { useEffect, useRef } from "react";

/** Екран замість потоку запису, коли заклад призупинено (ADR-018 D2). Без технічних деталей і без каталогу/слотів. */
export function TenantSuspended() {
  const heading = useRef<HTMLHeadingElement>(null);
  useEffect(() => heading.current?.focus(), []);
  return (
    <div className="mx-auto flex min-h-dvh w-full max-w-[480px] flex-col justify-center bg-[#F6F4F8] px-5 py-8 text-[#1E1B2E]">
      <main role="status" aria-live="polite" className="flex flex-col gap-3 rounded-2xl border-2 border-[#D9D4E4] bg-white p-5">
        <h1
          ref={heading}
          tabIndex={-1}
          className="font-[family-name:var(--font-unbounded)] text-xl font-semibold leading-tight focus:outline-none"
        >
          Запис тимчасово призупинено
        </h1>
        <p className="text-base text-[#5E5873]">Зверніться до салону іншим способом.</p>
      </main>
    </div>
  );
}
