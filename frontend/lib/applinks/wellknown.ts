/** Вміст `/.well-known/*` для universal links / App Links. `null` -> маршрут віддає 404 (env не задано). */

type Env = Record<string, string | undefined>;

const DEFAULT_IOS_BUNDLE_ID = "com.beautycrm.booking";

const clean = (v: string | undefined): string => (v ?? "").trim();

export function buildAppleAppSiteAssociation(env: Env = process.env): object | null {
  const teamId = clean(env.APPLE_TEAM_ID);
  if (!teamId) return null;
  const bundleId = clean(env.IOS_BUNDLE_ID) || DEFAULT_IOS_BUNDLE_ID;
  return {
    applinks: {
      apps: [],
      details: [{ appIDs: [`${teamId}.${bundleId}`], components: [{ "/": "/l/*" }], paths: ["/l/*"] }],
    },
  };
}

export function buildAssetLinks(env: Env = process.env): object | null {
  const pkg = clean(env.ANDROID_PACKAGE);
  const fingerprints = clean(env.ANDROID_SHA256_CERT_FINGERPRINTS)
    .split(/[,\s]+/)
    .filter(Boolean);
  if (!pkg || fingerprints.length === 0) return null;
  return [
    {
      relation: ["delegate_permission/common.handle_all_urls"],
      target: { namespace: "android_app", package_name: pkg, sha256_cert_fingerprints: fingerprints },
    },
  ];
}

/** Спільна відповідь route handler-ів: JSON або 404. */
export function wellKnownResponse(body: object | null): Response {
  if (!body) return new Response("Not found", { status: 404 });
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json", "Cache-Control": "public, max-age=300" },
  });
}
