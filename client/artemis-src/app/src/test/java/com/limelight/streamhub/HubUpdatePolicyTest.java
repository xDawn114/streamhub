package com.limelight.streamhub;

import com.google.gson.JsonObject;
import java.nio.charset.StandardCharsets;
import java.security.KeyPair;
import java.security.KeyPairGenerator;
import java.security.Signature;
import java.security.spec.MGF1ParameterSpec;
import java.security.spec.PSSParameterSpec;
import java.time.Instant;
import java.util.Base64;
import org.junit.BeforeClass;
import org.junit.Test;
import static org.junit.Assert.*;

public class HubUpdatePolicyTest {
    private static KeyPair key;
    private static HubUpdatePolicy.Trust trust;
    private static final long NOW = Instant.parse("2026-11-11T04:00:00Z").toEpochMilli();
    @BeforeClass public static void keys() throws Exception {
        KeyPairGenerator generator = KeyPairGenerator.getInstance("RSA"); generator.initialize(3072); key = generator.generateKeyPair();
        trust = new HubUpdatePolicy.Trust("https://updates.example.test/internal/android.json", Base64.getEncoder().encodeToString(key.getPublic().getEncoded()));
    }
    private JsonObject manifest() {
        JsonObject value = new JsonObject(); value.addProperty("schema",1); value.addProperty("product","StreamHub");
        value.addProperty("channel","internal");value.addProperty("architecture","android-arm64");value.addProperty("version","0.8.5");value.addProperty("versionCode",135);
        value.addProperty("minimumInstalledCode",134);value.addProperty("publishedAt","2026-11-11T03:00:00.0000000+00:00");value.addProperty("expiresAt","2026-11-12T03:00:00.0000000+00:00");
        value.addProperty("url","https://updates.example.test/packages/135.apk");value.addProperty("size",1024);value.addProperty("sha256",new String(new char[64]).replace('\0','A'));value.addProperty("notes","中文更新说明 😀");
        return value;
    }
    private byte[] signed(JsonObject value) throws Exception {
        byte[] bytes = value.toString().getBytes(StandardCharsets.UTF_8);
        Signature signer = Signature.getInstance("RSASSA-PSS"); signer.initSign(key.getPrivate());
        signer.setParameter(new PSSParameterSpec("SHA-256","MGF1",MGF1ParameterSpec.SHA256,32,1));signer.update(bytes);
        JsonObject envelope = new JsonObject();envelope.addProperty("payload",Base64.getEncoder().encodeToString(bytes));envelope.addProperty("signature",Base64.getEncoder().encodeToString(signer.sign()));
        return envelope.toString().getBytes(StandardCharsets.UTF_8);
    }
    private HubUpdatePolicy.Manifest read(byte[] bytes) throws Exception { return HubUpdatePolicy.read(bytes,trust,"internal","0.8.4",134,NOW); }
    private void reject(JsonObject value) throws Exception { try { read(signed(value)); fail("invalid manifest accepted"); } catch (java.io.IOException expected) { } }
    @Test public void signedUpgradeAndCurrentVersion() throws Exception {
        assertEquals(135,read(signed(manifest())).code);
        JsonObject current=manifest();current.addProperty("version","0.8.4");current.addProperty("versionCode",134);
        assertEquals(134,read(signed(current)).code);
    }
    @Test public void channelsDowngradesAndArchitectureCannotCross() throws Exception {
        JsonObject value=manifest();value.addProperty("channel","stable");reject(value);
        value=manifest();value.addProperty("architecture","win-x64");reject(value);
        value=manifest();value.addProperty("versionCode",133);reject(value);
        value=manifest();value.addProperty("version","0.8.3");reject(value);
        value=manifest();value.addProperty("versionCode",134);reject(value);
        value=manifest();value.addProperty("minimumInstalledCode",136);reject(value);
    }
    @Test public void originExpiryAndBoundaries() throws Exception {
        for(String url:new String[]{"http://updates.example.test/135.apk","https://evil.example.test/135.apk","https://user@updates.example.test/135.apk","https://updates.example.test:8443/135.apk","https://updates.example.test/135.apk#fragment"}) {
            JsonObject value=manifest();value.addProperty("url",url);reject(value);
        }
        JsonObject value=manifest();value.addProperty("expiresAt","2026-11-11T03:30:00Z");reject(value);
        value=manifest();value.addProperty("size",HubUpdatePolicy.MAX_PACKAGE+1);reject(value);
        value=manifest();value.addProperty("size",1.5);reject(value);
        value=manifest();value.addProperty("versionCode","135");reject(value);
        value=manifest();value.addProperty("sha256","not a hash");reject(value);
        value=manifest();value.addProperty("unknown",true);reject(value);
    }
    @Test public void corruptedBytesAndWrongKeyCannotPublish() throws Exception {
        byte[] bytes=signed(manifest()); bytes[bytes.length/3] ^= 1;
        try{read(bytes);fail();}catch(java.io.IOException expected){}
        KeyPairGenerator generator=KeyPairGenerator.getInstance("RSA");generator.initialize(3072);
        HubUpdatePolicy.Trust wrong=new HubUpdatePolicy.Trust(trust.feed,Base64.getEncoder().encodeToString(generator.generateKeyPair().getPublic().getEncoded()));
        try{HubUpdatePolicy.read(signed(manifest()),wrong,"internal","0.8.4",134,NOW);fail();}catch(java.io.IOException expected){}
        assertFalse(new HubUpdatePolicy.Trust("", "").configured());
    }
    @Test public void scheduleAndBusyInstallGuards() {
        assertTrue(HubUpdateSchedule.due(true,0,NOW));assertFalse(HubUpdateSchedule.due(false,0,NOW));
        assertFalse(HubUpdateSchedule.due(true,NOW+HubUpdateSchedule.INTERVAL,NOW));
        assertEquals(300000,HubUpdateSchedule.retryDelay(0));assertEquals(43200000,HubUpdateSchedule.retryDelay(4));
        assertFalse(HubUpdateSchedule.canInstall(true,false,false));assertFalse(HubUpdateSchedule.canInstall(false,true,false));
        assertFalse(HubUpdateSchedule.canInstall(false,false,true));assertTrue(HubUpdateSchedule.canInstall(false,false,false));
    }
    @Test public void readsDotNetPssCrossPlatformVector() throws Exception {
        try(java.io.InputStream input=getClass().getResourceAsStream("/streamhub-update-dotnet-vector.json")) {
            assertNotNull(input);JsonObject vector=com.google.gson.JsonParser.parseReader(new java.io.InputStreamReader(input,StandardCharsets.UTF_8)).getAsJsonObject();
            HubUpdatePolicy.Trust source=new HubUpdatePolicy.Trust(trust.feed,vector.get("publicKey").getAsString());
            HubUpdatePolicy.Manifest parsed=HubUpdatePolicy.read(Base64.getDecoder().decode(vector.get("envelope").getAsString()),source,"internal","0.8.4",134,NOW);
            assertEquals(135,parsed.code);assertEquals("跨端签名互通 😀",parsed.notes);
        }
    }
}
