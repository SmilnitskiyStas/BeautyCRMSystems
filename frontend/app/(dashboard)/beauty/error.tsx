"use client";

export default function BeautyError({ reset }: { error: Error & { digest?: string }; reset: () => void }) {
  return (
    <div role="alert" className="flex flex-wrap items-center gap-3 rounded-2xl bg-white p-6 text-sm">
      <span>Щось пішло не так. Спробуйте ще раз.</span>
      <button type="button" onClick={reset} className="min-h-11 cursor-pointer rounded-[22px] border border-(--line-strong) bg-white px-5 text-sm font-semibold">
        Спробувати ще раз
      </button>
    </div>
  );
}
