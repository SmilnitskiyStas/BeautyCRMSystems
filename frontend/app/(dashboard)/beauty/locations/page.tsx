import type { Metadata } from "next";
import { LocationsScreen } from "@/features/beauty-admin/components/locations-screen";

export const metadata: Metadata = { title: "Заклади" };

export default function Page() {
  return <LocationsScreen />;
}
