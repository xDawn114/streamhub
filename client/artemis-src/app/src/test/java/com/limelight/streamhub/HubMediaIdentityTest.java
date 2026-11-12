package com.limelight.streamhub;

import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import java.math.BigInteger;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.cert.X509Certificate;
import java.util.Date;
import org.bouncycastle.asn1.x500.X500Name;
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter;
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder;
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=android.app.Application.class)
public class HubMediaIdentityTest {
    private static X509Certificate certificate() throws Exception {
        KeyPairGenerator generator = KeyPairGenerator.getInstance("RSA"); generator.initialize(2048);
        KeyPair key = generator.generateKeyPair(); X500Name name = new X500Name("CN=Local media identity test");
        long now = System.currentTimeMillis();
        return new JcaX509CertificateConverter().getCertificate(new JcaX509v3CertificateBuilder(name,
                BigInteger.valueOf(now), new Date(now - 60000), new Date(now + 3600000), name, key.getPublic())
                .build(new JcaContentSignerBuilder("SHA256withRSA").build(key.getPrivate())));
    }
    @Test public void authenticatedExistingIdentityRestoresMissingServerPin() throws Exception {
        X509Certificate client = certificate(), server = certificate();
        X509Certificate recovered = HubMediaIdentity.recover(body -> {
            assertEquals("tablet", body.getString("mode"));
            assertArrayEquals(client.getEncoded(), java.util.Base64.getDecoder().decode(body.getString("clientCertificate")));
            return new JSONObject().put("paired", true).put("serverCertificate",
                    java.util.Base64.getEncoder().encodeToString(server.getEncoded()));
        }, "tablet", client);
        assertArrayEquals(server.getEncoded(), recovered.getEncoded());
    }
    @Test public void unknownIdentityDoesNotCreateTrustOrUseReturnedCertificate() throws Exception {
        assertNull(HubMediaIdentity.recover(body -> new JSONObject().put("paired", false)
                .put("serverCertificate", "untrusted-data"), "remote", certificate()));
    }
    @Test public void revokedIdentityErrorNeverFallsBackToPinPairing() throws Exception {
        try {
            HubMediaIdentity.recover(body -> { throw new HubFileRetryPolicy.HttpFailure(403, "revoked"); }, "remote", certificate());
            fail("authorization error hidden");
        } catch (HubFileRetryPolicy.HttpFailure expected) { assertEquals(403, expected.status); }
    }
    @Test public void olderPcCanContinueItsNormalHandshake() throws Exception {
        assertNull(HubMediaIdentity.recover(body -> { throw new HubFileRetryPolicy.HttpFailure(404, "unsupported"); }, "remote", certificate()));
    }
    @Test public void authorizedResponseStillRequiresARealServerCertificate() throws Exception {
        for (JSONObject response : new JSONObject[] { new JSONObject(), new JSONObject().put("paired", "true"),
                new JSONObject().put("paired", true), new JSONObject().put("paired", true).put("serverCertificate", "not-base64") }) {
            try { HubMediaIdentity.recover(body -> response, "remote", certificate()); fail("invalid identity accepted"); }
            catch (java.io.IOException expected) { }
        }
    }
}
