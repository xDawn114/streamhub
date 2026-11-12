package com.limelight.streamhub;

import android.util.Base64;
import org.json.JSONObject;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.Locale;
import java.util.concurrent.TimeUnit;
import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

/** HTTPS is authenticated by the certificate fingerprint transferred with the PC binding code. */
public final class HubControlClient {
    private final OkHttpClient client;
    private final String host, token;
    private final int port;
    public HubControlClient(String host, String invitation) throws Exception {
        this.host = host.trim();
        JSONObject code = new JSONObject(new String(Base64.decode(invitation.trim(), Base64.URL_SAFE | Base64.NO_WRAP), StandardCharsets.UTF_8));
        if (code.getInt("v") != 1) throw new IOException("绑定码版本不支持，请更新两端。");
        port = code.getInt("port"); token = code.getString("token");
        final String pin = code.getString("pin").toUpperCase(Locale.ROOT);
        if (port < 1 || port > 65535 || !pin.matches("[A-F0-9]{64}") || !token.matches("[A-Fa-f0-9]{64}")) throw new IOException("绑定码不完整，请重新复制。");
        X509TrustManager trust = new X509TrustManager() {
            @Override public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException { throw new CertificateException("不接受客户端证书"); }
            @Override public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
                try {
                    if (chain == null || chain.length == 0 || !MessageDigest.isEqual(pin.getBytes(StandardCharsets.US_ASCII), hex(MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded())).getBytes(StandardCharsets.US_ASCII)))
                        throw new CertificateException("电脑证书与绑定码不一致，请在电脑端重新确认。");
                    chain[0].checkValidity();
                } catch (CertificateException e) { throw e; }
                catch (Exception e) { throw new CertificateException("电脑证书校验失败", e); }
            }
            @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
        };
        SSLContext ssl = SSLContext.getInstance("TLS"); ssl.init(null, new TrustManager[]{trust}, new SecureRandom());
        client = new OkHttpClient.Builder().proxy(java.net.Proxy.NO_PROXY).sslSocketFactory(ssl.getSocketFactory(), trust)
                .hostnameVerifier((hostname, session) -> hostname.equals(this.host))
                .connectTimeout(4, TimeUnit.SECONDS).readTimeout(12, TimeUnit.SECONDS).writeTimeout(10, TimeUnit.SECONDS)
                .callTimeout(18, TimeUnit.SECONDS).build();
    }
    public static String hex(byte[] bytes) { StringBuilder text = new StringBuilder(); for (byte b : bytes) text.append(String.format(Locale.ROOT, "%02X", b)); return text.toString(); }
    public void cancel() { client.dispatcher().cancelAll(); }
    static HubFileRetryPolicy.HttpFailure httpFailure(int status, String text, String fallback) {
        String message = fallback + "（" + status + "）。";
        try {
            String parsed = new JSONObject(text).optString("message", "");
            if (!parsed.isEmpty()) message = parsed;
        } catch (Exception ignored) { }
        return new HubFileRetryPolicy.HttpFailure(status, message);
    }
    public JSONObject fileJson(String path, JSONObject body, String clientId, String key) throws Exception {
        Request.Builder request = fileRequest(path, clientId, key);
        if (path.startsWith("files/tasks/") && path.endsWith("/complete")) request.header("X-StreamHub-Completion", "async-v1");
        if (body != null) request.post(RequestBody.create(body.toString(), MediaType.parse("application/json; charset=utf-8")));
        try (Response response = client.newCall(request.build()).execute()) {
            if (!response.isSuccessful()) throw httpFailure(response.code(), response.body() == null ? "" : response.body().string(), "文件请求未完成");
            if (response.body() == null) throw new IOException("电脑未返回文件结果。");
            JSONObject result = new JSONObject(response.body().string());
            if (!result.optBoolean("success"))
                throw new HubFileRetryPolicy.HttpFailure(response.code(), result.optString("message", "文件请求未完成。"));
            return result;
        }
    }
    public byte[] fileData(String id, long offset, byte[] upload, String clientId, String key) throws Exception {
        if (!id.matches("[A-Za-z0-9_-]{8,128}") || offset < 0) throw new IOException("文件任务或分块位置无效。");
        Request.Builder request = fileRequest("files/tasks/" + id + "/data", clientId, key).header("X-Offset", Long.toString(offset));
        if (upload != null) {
            if (upload.length < 1 || upload.length > 256 * 1024) throw new IOException("文件块长度无效。");
            request.put(RequestBody.create(upload, MediaType.parse("application/octet-stream")));
        }
        try (Response response = client.newCall(request.build()).execute()) {
            if (!response.isSuccessful()) {
                throw httpFailure(response.code(), response.body() == null ? "" : response.body().string(), "文件块请求失败");
            }
            if (response.body() == null) throw new IOException("文件块为空。");
            if (upload != null) {
                JSONObject result = new JSONObject(response.body().string());
                if (!result.optBoolean("success")) throw new IOException("分块未确认。"); return new byte[0];
            }
            if (response.body().contentLength() < 0 || response.body().contentLength() > 256 * 1024) throw new IOException("电脑文件块长度无效。");
            return response.body().bytes();
        }
    }
    private Request.Builder fileRequest(String path, String clientId, String key) {
        return new Request.Builder().url(new okhttp3.HttpUrl.Builder().scheme("https").host(host).port(port).addPathSegments("v1/" + path).build())
            .header("Authorization", "Bearer " + token).header("X-StreamHub-Client", clientId).header("X-StreamHub-Key", key);
    }
    /** Address discovery must not borrow the longer preparation/pairing time budget. */
    public JSONObject probeInfo() throws Exception {
        return request("info", null, client.newBuilder().connectTimeout(2, TimeUnit.SECONDS)
                .readTimeout(3, TimeUnit.SECONDS).callTimeout(3, TimeUnit.SECONDS).build());
    }
    public JSONObject request(String path, JSONObject body) throws Exception {
        return request(path, body, client);
    }
    /** Keep periodic lease work bounded so one stalled request cannot hide later heartbeats. */
    public JSONObject requestLease(String path, JSONObject body) throws Exception {
        return request(path, body, client.newBuilder().connectTimeout(2, TimeUnit.SECONDS)
                .readTimeout(3, TimeUnit.SECONDS).callTimeout(3, TimeUnit.SECONDS).build());
    }

    /** Bounded independently from preparation, requests and cleanup. */
    public JSONObject requestHeartbeat(JSONObject body) throws Exception {
        return request("session/heartbeat", body, client.newBuilder().connectTimeout(1, TimeUnit.SECONDS)
                .readTimeout(1500, TimeUnit.MILLISECONDS).callTimeout(1500, TimeUnit.MILLISECONDS).build());
    }
    /** Execute one explicit clipboard action during the authenticated stream lease. */
    public JSONObject clipboard(String mode, String leaseId, String clientId, String action, JSONObject payload) throws Exception {
        if (!("remote".equals(mode) || "tablet".equals(mode)) || leaseId == null || leaseId.isEmpty() ||
                clientId == null || clientId.isEmpty() || !("get".equals(action) || "set".equals(action)) ||
                ("set".equals(action) != (payload != null)))
            throw new IOException("剪贴板会话参数无效。");
        JSONObject body = new JSONObject().put("mode", mode).put("leaseId", leaseId)
                .put("clientId", clientId).put("action", action);
        if (payload != null) body.put("payload", payload);
        return request("clipboard", body);
    }
    private JSONObject request(String path, JSONObject body, OkHttpClient transport) throws Exception {
        okhttp3.HttpUrl url = new okhttp3.HttpUrl.Builder().scheme("https").host(host).port(port).addPathSegments("v1/" + path).build();
        Request.Builder request = new Request.Builder().url(url).header("Authorization", "Bearer " + token);
        if (body != null) request.post(RequestBody.create(body.toString(), MediaType.parse("application/json; charset=utf-8")));
        try (Response response = transport.newCall(request.build()).execute()) {
            if (!response.isSuccessful()) throw httpFailure(response.code(), response.body() == null ? "" : response.body().string(), "电脑请求未完成");
            if (response.body() == null) throw new IOException("电脑没有返回结果。");
            JSONObject data = new JSONObject(response.body().string());
            if (!data.optBoolean("success"))
                throw new HubFileRetryPolicy.HttpFailure(response.code(), data.optString("message", "电脑请求未完成（" + response.code() + "）。"));
            return data;
        }
    }
}
