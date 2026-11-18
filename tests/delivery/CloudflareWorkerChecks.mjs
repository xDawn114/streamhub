import assert from "node:assert/strict";
import { serve } from "../../deployment/cloudflare/worker.mjs";

let calls = [];
const fetcher = async (url, options) => {
  calls.push({ url, options });
  return new Response("{\"signed\":true}", { headers: { "Cache-Control": "max-age=600" } });
};
const request = (path, method = "GET") => new Request("https://updates.example" + path, { method });
const manifests = { "/updates/internal/android.json": "{\"signed\":true}" };
const feed = await serve(request("/updates/internal/android.json"), fetcher, manifests);
assert.equal(feed.status, 200);
assert.match(feed.headers.get("Cache-Control"), /no-store/);
assert.equal(await feed.text(), "{\"signed\":true}");
assert.equal(calls.length, 0); // A feed is never fetched through an upstream CDN.
assert.equal((await serve(request("/updates/internal/android.json"), fetcher)).status, 503);
assert.equal((await serve(request("/updates/internal/android.json"), fetcher,
  { "/updates/internal/android.json": "x".repeat(65537) })).status, 503);
const packagePath = "/packages/internal/0.8.26-168/" + "a".repeat(64) + "/android.apk";
const apk = await serve(request(packagePath), fetcher);
assert.equal(apk.status, 200);
assert.match(apk.headers.get("Cache-Control"), /immutable/);
assert.equal(calls[0].url, "https://xdawn114.github.io/streamhub" + packagePath);
assert.equal(calls[0].options.redirect, "manual");
const allowedCalls = calls.length;
for (const path of ["/credentials", "/updates/stable/android.json", "/updates/internal/android.json?url=https://evil.example",
  "/packages/internal/0.8.26-168/" + "a".repeat(64) + "/windows.exe", "/updates/internal/%61ndroid.json"]) {
  assert.equal((await serve(request(path), fetcher)).status, 404);
}
assert.equal((await serve(request("/updates/internal/android.json", "POST"), fetcher)).status, 405);
assert.equal(calls.length, allowedCalls);
const redirectFailure = await serve(request(packagePath), async () => new Response(null, { status: 302 }));
assert.equal(redirectFailure.status, 502);
assert.equal(redirectFailure.headers.get("X-Update-Upstream-Status"), "302");
const fetchFailure = await serve(request(packagePath), async () => { throw new Error("upstream unavailable"); });
assert.equal(fetchFailure.status, 502);
assert.equal(fetchFailure.headers.get("X-Update-Upstream-Error"), "fetch-failed");
console.log("Cloudflare worker boundary checks passed");
