import { connection } from "next/server";
import { buildAssetLinks, wellKnownResponse } from "@/lib/applinks/wellknown";

/** Env читається на запит, не під час збірки. */
export async function GET() {
  await connection();
  return wellKnownResponse(buildAssetLinks());
}
