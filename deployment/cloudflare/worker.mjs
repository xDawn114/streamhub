// Public update artifacts only. No keys, device information or arbitrary proxy.
const ORIGIN = "https://xdawn114.github.io";
const PREFIX = "/streamhub";
const FEED = /^\/updates\/internal\/(android|windows)\.json$/;
const PACKAGE = /^\/packages\/internal\/[0-9]+\.[0-9]+\.[0-9]+-[0-9]+\/[0-9a-f]{64}\/android\.apk$/;
const NO_STORE = "no-store, no-cache, max-age=0, must-revalidate";
// Replaced only by Build-CloudflareWorker.ps1 after publication verification.
const manifests = /* SIGNED_MANIFESTS */ Object.freeze({});

function response(body, status, extra = {}) {
  return new Response(body, { status, headers: {
    "Cache-Control": NO_STORE,
    "X-Content-Type-Options": "nosniff",
    ...extra,
  }});
}

export async function serve(request, fetchUpstream = fetch, signedManifests = manifests) {
  const url = new URL(request.url);
  if (!["GET", "HEAD"].includes(request.method)) return response(null, 405, { Allow: "GET, HEAD" });
  if (url.search || url.pathname.includes("%")) return response(null, 404);
  if (url.pathname === "/") return response(request.method === "HEAD" ? null : "iControl internal updates\n", 200,
    { "Content-Type": "text/plain; charset=utf-8" });
  const isFeed = FEED.test(url.pathname);
  if (!isFeed && !PACKAGE.test(url.pathname)) return response(null, 404);
  if (isFeed) {
    const body = signedManifests[url.pathname];
    if (typeof body !== "string" || !body.length || new TextEncoder().encode(body).length > 65536) return response(null, 503);
    return response(request.method === "HEAD" ? null : body, 200,
      { "Content-Type": "application/json; charset=utf-8" });
  }
  const upstream = ORIGIN + PREFIX + url.pathname;
  try {
    const result = await fetchUpstream(upstream, {
      method: request.method,
      redirect: "error",
      headers: { "Accept-Encoding": "identity" },
      cf: { cacheTtl: 31536000, cacheEverything: true },
    });
    if (result.status !== 200 || result.redirected) return response(null, result.status === 404 ? 404 : 502);
    const headers = new Headers({
      "Cache-Control": "public, max-age=31536000, immutable",
      "Content-Type": "application/vnd.android.package-archive",
      "X-Content-Type-Options": "nosniff",
    });
    const size = result.headers.get("Content-Length");
    if (size) headers.set("Content-Length", size);
    return new Response(request.method === "HEAD" ? null : result.body, { status: 200, headers });
  } catch {
    return response(null, 502);
  }
}

export default { fetch: request => serve(request) };
