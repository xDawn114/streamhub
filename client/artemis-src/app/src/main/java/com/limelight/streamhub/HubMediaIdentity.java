package com.limelight.streamhub;

import org.json.JSONObject;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;

/** Recover only a media identity already authorized by the bound PC control channel. */
final class HubMediaIdentity {
    interface Query { JSONObject request(JSONObject body) throws Exception; }
    private HubMediaIdentity() { }

    static X509Certificate recover(Query query, String mode, X509Certificate clientCertificate) throws Exception {
        if (!("remote".equals(mode) || "tablet".equals(mode)) || clientCertificate == null)
            throw new IOException("媒体身份参数无效。");
        byte[] clientDer = clientCertificate.getEncoded();
        if (clientDer.length == 0 || clientDer.length > 16384) throw new IOException("客户端证书大小无效。");
        JSONObject response;
        try {
            response = query.request(new JSONObject().put("mode", mode)
                    .put("clientCertificate", java.util.Base64.getEncoder().encodeToString(clientDer)));
        } catch (HubFileRetryPolicy.HttpFailure error) {
            // Older bound PCs retain their existing PIN handshake; other failures stay visible.
            if (error.status == 404) return null;
            throw error;
        }
        if (!(response.opt("paired") instanceof Boolean)) throw new IOException("电脑媒体身份响应无效。");
        if (!response.getBoolean("paired")) return null;
        String encoded = response.optString("serverCertificate", "");
        if (encoded.isEmpty() || encoded.length() > 24576) throw new IOException("电脑没有返回有效的媒体证书。");
        byte[] der;
        try { der = java.util.Base64.getDecoder().decode(encoded); }
        catch (IllegalArgumentException error) { throw new IOException("电脑媒体证书格式无效。", error); }
        if (der.length == 0 || der.length > 16384) throw new IOException("电脑媒体证书大小无效。");
        try (ByteArrayInputStream input = new ByteArrayInputStream(der)) {
            X509Certificate certificate = (X509Certificate) CertificateFactory.getInstance("X.509").generateCertificate(input);
            if (input.available() != 0) throw new IOException("电脑媒体证书包含多余数据。");
            certificate.checkValidity();
            return certificate;
        }
    }
}
