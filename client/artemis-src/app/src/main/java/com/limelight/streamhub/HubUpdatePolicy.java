package com.limelight.streamhub;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import java.io.IOException;
import java.net.URI;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.security.KeyFactory;
import java.security.Signature;
import java.security.interfaces.RSAPublicKey;
import java.security.spec.MGF1ParameterSpec;
import java.security.spec.PSSParameterSpec;
import java.security.spec.X509EncodedKeySpec;
import java.time.OffsetDateTime;
import java.util.Base64;

/** Same signed envelope and version/origin rules as the Windows updater. No network-supplied keys. */
public final class HubUpdatePolicy {
    public static final int MAX_MANIFEST = 64 * 1024;
    public static final long MAX_PACKAGE = 1024L * 1024 * 1024;
    public static final class Trust {
        public final String feed, publicKey;
        public Trust(String feed, String publicKey) { this.feed = feed; this.publicKey = publicKey; }
        public boolean configured() { return https(feed) && publicKey != null && publicKey.length() > 64 && publicKey.length() < 2048; }
    }
    public static final class Manifest {
        public final String version, channel, url, sha256, notes;
        public final int code;
        public final long size, expiresAt;
        private Manifest(JsonObject value) throws Exception {
            version = text(value, "version"); channel = text(value, "channel"); url = text(value, "url");
            sha256 = text(value, "sha256"); notes = text(value, "notes");
            code = Math.toIntExact(number(value, "versionCode")); size = number(value, "size");
            expiresAt = time(value, "expiresAt");
        }
        public void requireFresh(long now) throws IOException {
            if (expiresAt <= now) throw new IOException("更新清单已过期，请重新检查更新。");
        }
    }
    private HubUpdatePolicy() { }
    public static Manifest read(byte[] envelope, Trust trust, String channel, String version, int code, long now) throws IOException {
        if (!trust.configured()) throw new IOException("更新发布源尚未接入，当前使用手动安装。");
        try {
            if (envelope.length == 0 || envelope.length > MAX_MANIFEST) throw new IOException();
            JsonObject signed = JsonParser.parseString(utf8(envelope)).getAsJsonObject();
            if (signed.size() != 2) throw new IOException();
            byte[] payload = canonicalBase64(text(signed, "payload")), signature = canonicalBase64(text(signed, "signature"));
            if (payload.length == 0 || payload.length > 32768 || signature.length < 256 || signature.length > 1024) throw new IOException();
            RSAPublicKey key = (RSAPublicKey) KeyFactory.getInstance("RSA").generatePublic(new X509EncodedKeySpec(Base64.getDecoder().decode(trust.publicKey)));
            if (key.getModulus().bitLength() < 3072) throw new IOException();
            Signature verifier;
            try { verifier = Signature.getInstance("RSASSA-PSS"); }
            catch (java.security.NoSuchAlgorithmException unavailable) { verifier = Signature.getInstance("SHA256withRSA/PSS"); }
            verifier.initVerify(key);
            verifier.setParameter(new PSSParameterSpec("SHA-256", "MGF1", MGF1ParameterSpec.SHA256, 32, 1));
            verifier.update(payload);
            if (!verifier.verify(signature)) throw new IOException();
            JsonObject value = JsonParser.parseString(utf8(payload)).getAsJsonObject();
            if (value.size() != 13 || number(value, "schema") != 1 || !"StreamHub".equals(text(value, "product")) ||
                    !"android-arm64".equals(text(value, "architecture"))) throw new IOException();
            Manifest result = new Manifest(value);
            long published = time(value, "publishedAt"), minimum = number(value, "minimumInstalledCode");
            int comparison = compareVersions(result.version, version);
            if (!channel.equals(result.channel) || result.code < code || comparison < 0 || (result.code == code && comparison != 0) ||
                    minimum < 1 || minimum > code || result.size < 1 || result.size > MAX_PACKAGE ||
                    !result.sha256.matches("[A-Fa-f0-9]{64}") || result.notes.length() > 4096 ||
                    published > now + 300000 || result.expiresAt <= now || result.expiresAt <= published ||
                    result.expiresAt - published > 90L * 86400000 || !sameOrigin(trust.feed, result.url)) throw new IOException();
            return result;
        } catch (Exception error) { throw new IOException("更新清单签名、版本或下载地址无效，已停止更新。", error); }
    }
    public static boolean https(String value) {
        try {
            URI uri = new URI(value);
            return "https".equalsIgnoreCase(uri.getScheme()) && uri.getHost() != null &&
                    (uri.getPort() == -1 || uri.getPort() == 443) && uri.getRawUserInfo() == null && uri.getRawFragment() == null;
        } catch (Exception invalid) { return false; }
    }
    public static boolean sameOrigin(String feed, String url) {
        return https(feed) && https(url) && URI.create(feed).getHost().equalsIgnoreCase(URI.create(url).getHost());
    }
    public static int compareVersions(String left, String right) throws IOException {
        if (!left.matches("[0-9]+\\.[0-9]+\\.[0-9]+") || !right.matches("[0-9]+\\.[0-9]+\\.[0-9]+")) throw new IOException();
        String[] a = left.split("\\."), b = right.split("\\.");
        try { for (int i = 0; i < 3; i++) { int order = Integer.compare(Integer.parseInt(a[i]), Integer.parseInt(b[i])); if (order != 0) return order; } }
        catch (NumberFormatException invalid) { throw new IOException(invalid); }
        return 0;
    }
    private static String text(JsonObject value, String name) throws IOException {
        if (!value.has(name) || !value.get(name).isJsonPrimitive() || !value.getAsJsonPrimitive(name).isString()) throw new IOException();
        return value.get(name).getAsString();
    }
    private static long number(JsonObject value, String name) throws IOException {
        if (!value.has(name) || !value.get(name).isJsonPrimitive() || !value.getAsJsonPrimitive(name).isNumber() ||
                !value.get(name).getAsString().matches("[0-9]+")) throw new IOException();
        return Long.parseLong(value.get(name).getAsString());
    }
    private static long time(JsonObject value, String name) throws IOException { return OffsetDateTime.parse(text(value, name)).toInstant().toEpochMilli(); }
    private static byte[] canonicalBase64(String value) throws IOException {
        byte[] bytes = Base64.getDecoder().decode(value);
        if (!Base64.getEncoder().encodeToString(bytes).equals(value)) throw new IOException();
        return bytes;
    }
    private static String utf8(byte[] bytes) throws Exception {
        return StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString();
    }
}
