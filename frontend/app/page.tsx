import Link from "next/link";

const links = [
  { href: "/beauty", title: "Адмінка", text: "Огляд мережі, календар, клієнти, акції, аналітика, AI, канали" },
  { href: "/book", title: "Онлайн-запис клієнта", text: "Публічний потік запису (mobile-first)" },
];

export default function Home() {
  return (
    <main className="mx-auto flex min-h-dvh w-full max-w-xl flex-col justify-center gap-4 px-5 py-10 text-[#1E1B2E]">
      <h1 className="text-2xl font-semibold">Beauty CRM</h1>
      <ul className="flex flex-col gap-3">
        {links.map((l) => (
          <li key={l.href}>
            <Link
              href={l.href}
              className="flex min-h-11 flex-col gap-1 rounded-2xl border border-[#D9D4E4] bg-white p-4 hover:border-[#6B2F5C] focus-visible:outline-2 focus-visible:outline-[#6B2F5C]"
            >
              <span className="font-semibold">{l.title}</span>
              <span className="text-sm text-[#5E5873]">{l.text}</span>
            </Link>
          </li>
        ))}
      </ul>
    </main>
  );
}
