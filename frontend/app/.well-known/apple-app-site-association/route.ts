import { connection } from "next/server";
import { buildAppleAppSiteAssociation, wellKnownResponse } from "@/lib/applinks/wellknown";

/** AASA без розширення, `application/json`, без редиректів (вимога Apple). Env читається на запит, не під час збірки. */
export async function GET() {
  await connection();
  return wellKnownResponse(buildAppleAppSiteAssociation());
}
