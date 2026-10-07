import type { Metadata } from "next";
import { StaffListScreen } from "@/features/beauty-admin/components/staff-screens";

export const metadata: Metadata = { title: "Спеціалісти" };

export default function Page() {
  return <StaffListScreen />;
}
