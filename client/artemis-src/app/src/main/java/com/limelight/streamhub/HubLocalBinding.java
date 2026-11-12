package com.limelight.streamhub;

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

/** Enrollment has its own bounded, unauthenticated transport; no stored token is attached. */
public final class HubLocalBinding {
    private final HubLanDiscovery.Device device;
    private final OkHttpClient client;
    private final String nonce;
    /** A fresh TLS connection may bootstrap public identity only. Approval still requires the
     * certificate-bound comparison on both devices; no stored token is used by this class. */
    public static HubLanDiscovery.Device lookup(String address) throws Exception {
        if (!HubLanDiscovery.localIpv4(address)) throw new IOException("请输入电脑显示的局域网 IPv4 地址。");
        return lookupAt(address, 49199);
    }
    // Separate endpoint for loopback TLS tests. The UI uses only lookup(), with its LAN restriction.
    static HubLanDiscovery.Device lookupAt(String address, int port) throws Exception {
        PinTrust trust = new PinTrust(null);
        OkHttpClient probe = transport(address, trust);
        try {
            okhttp3.HttpUrl url = new okhttp3.HttpUrl.Builder().scheme("https").host(address).port(port)
                    .addPathSegments("v1/binding/identity").build();
            JSONObject identity = read(probe, new Request.Builder().url(url).get().build());
            return identity(address, identity, trust.pin);
        } finally { probe.dispatcher().cancelAll(); probe.connectionPool().evictAll(); }
    }
    static HubLanDiscovery.Device identity(String address, JSONObject value, String certificatePin) throws Exception {
        String id = value.optString("id"), name = value.optString("name"), pin = value.optString("pin");
        if (value.optInt("v") != 1 || value.optInt("port") != 49199
                || !id.matches("(?i)[a-f0-9]{8}(-[a-f0-9]{4}){3}-[a-f0-9]{12}")
                || name.isEmpty() || name.length() > 64 || name.chars().anyMatch(Character::isISOControl)
                || !pin.matches("(?i)[a-f0-9]{64}") || !pin.equalsIgnoreCase(certificatePin))
            throw new IOException("电脑身份与连接证书不一致，原绑定已保留。");
        return new HubLanDiscovery.Device("address", id, name, address, pin.toUpperCase(Locale.ROOT));
    }
    static final class PinTrust implements X509TrustManager {
        private final String expected;
        String pin;
        PinTrust(String expected) { this.expected = expected; }
        @Override public void checkClientTrusted(X509Certificate[] chain, String type) throws CertificateException { throw new CertificateException(); }
        @Override public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
        @Override public void checkServerTrusted(X509Certificate[] chain, String type) throws CertificateException {
            try {
                if (chain == null || chain.length == 0) throw new CertificateException("电脑没有返回证书。");
                chain[0].checkValidity();
                String received = HubControlClient.hex(MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded()));
                String required = expected == null ? pin : expected;
                if (required != null && !MessageDigest.isEqual(required.toUpperCase(Locale.ROOT).getBytes(StandardCharsets.US_ASCII),
                        received.getBytes(StandardCharsets.US_ASCII))) throw new CertificateException("电脑证书已改变，请重新查找。");
                pin = received;
            } catch (CertificateException error) { throw error; } catch (Exception error) { throw new CertificateException(error); }
        }
    }
    private static OkHttpClient transport(String address, PinTrust trust) throws Exception {
        SSLContext tls = SSLContext.getInstance("TLS"); tls.init(null, new TrustManager[]{trust}, new SecureRandom());
        return new OkHttpClient.Builder().proxy(java.net.Proxy.NO_PROXY).sslSocketFactory(tls.getSocketFactory(), trust)
                .hostnameVerifier((host, session) -> host.equals(address)).followRedirects(false).followSslRedirects(false)
                .retryOnConnectionFailure(false).connectTimeout(3, TimeUnit.SECONDS).readTimeout(4, TimeUnit.SECONDS)
                .callTimeout(5, TimeUnit.SECONDS).build();
    }
    public HubLocalBinding(HubLanDiscovery.Device device) throws Exception {
        if(!HubLanDiscovery.localIpv4(device.address) || !device.pin.matches("[A-Fa-f0-9]{64}"))throw new IOException("附近电脑信息无效，请重新查找。");
        this.device=device;
        byte[] random=new byte[32];new SecureRandom().nextBytes(random);nonce=HubControlClient.hex(random);
        client = transport(device.address, new PinTrust(device.pin));
    }
    public JSONObject begin(String code,String clientId,String name)throws Exception{
        JSONObject result=post("request",new JSONObject().put("code",code).put("clientId",clientId).put("name",name).put("nonce",nonce));
        String id=result.getString("requestId");
        byte[] hash=MessageDigest.getInstance("SHA-256").digest((device.pin+":"+nonce+":"+id).getBytes(StandardCharsets.UTF_8));
        long value=((long)(hash[0]&255)<<24)|((long)(hash[1]&255)<<16)|((long)(hash[2]&255)<<8)|(hash[3]&255);
        if(!String.format(Locale.ROOT,"%06d",value%1000000).equals(result.getString("comparison")))throw new IOException("设备校验码不一致，请取消后重新绑定。");
        return result;
    }
    public JSONObject poll(String id)throws Exception{return post("poll",new JSONObject().put("requestId",id).put("nonce",nonce));}
    public String verifiedInvitation(JSONObject result)throws Exception{
        String invitation=result.getString("invitation");
        JSONObject trust=new JSONObject(new String(android.util.Base64.decode(invitation,android.util.Base64.URL_SAFE|android.util.Base64.NO_WRAP),StandardCharsets.UTF_8));
        if(!device.pin.equalsIgnoreCase(trust.getString("pin")) || trust.getInt("port")!=49199)throw new IOException("电脑确认与连接证书不一致，原绑定已保留。");
        return invitation;
    }
    private JSONObject post(String action,JSONObject body)throws Exception{
        okhttp3.HttpUrl url=new okhttp3.HttpUrl.Builder().scheme("https").host(device.address).port(49199).addPathSegments("v1/binding/"+action).build();
        Request request=new Request.Builder().url(url).post(RequestBody.create(body.toString(),MediaType.parse("application/json; charset=utf-8"))).build();
        return read(client, request);
    }
    private static JSONObject read(OkHttpClient transport, Request request) throws Exception {
        try(Response response=transport.newCall(request).execute()){
            if(response.isRedirect())throw new IOException("电脑确认接口发生跳转，原绑定已保留。");
            if(response.body()==null || response.body().contentLength()>65536)throw new IOException("电脑返回的确认信息无效。");
            try(java.io.InputStream stream=response.body().byteStream();java.io.ByteArrayOutputStream bytes=new java.io.ByteArrayOutputStream()){
                byte[] buffer=new byte[4096];int read;
                while((read=stream.read(buffer))!=-1){if(bytes.size()+read>65536)throw new IOException("电脑返回的确认信息过大。");bytes.write(buffer,0,read);}
                JSONObject result=new JSONObject(bytes.toString("UTF-8"));
                if(!response.isSuccessful() || !result.optBoolean("success"))throw new IOException(result.optString("message","绑定未完成，请重试。"));
                return result.getJSONObject("result");
            }
        }
    }
    public void cancel(){
        // TLS close may write close_notify. Cancellation is called by dialog buttons,
        // so both active-call cancellation and pooled-socket cleanup must leave the UI thread.
        if (android.os.Looper.myLooper() == android.os.Looper.getMainLooper()) {
            new Thread(this::cancelTransport, "Hub-Binding-Cancel").start();
        } else cancelTransport();
    }
    private void cancelTransport(){client.dispatcher().cancelAll();client.connectionPool().evictAll();}
}
