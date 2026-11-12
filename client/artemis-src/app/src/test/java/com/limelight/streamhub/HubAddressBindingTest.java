package com.limelight.streamhub;

import android.app.Application;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.bouncycastle.asn1.x500.X500Name;
import org.bouncycastle.cert.jcajce.JcaX509CertificateConverter;
import org.bouncycastle.cert.jcajce.JcaX509v3CertificateBuilder;
import org.bouncycastle.operator.jcajce.JcaContentSignerBuilder;
import javax.net.ssl.*;
import java.io.*;
import java.math.BigInteger;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.security.cert.*;
import java.util.*;
import java.util.concurrent.*;
import static org.junit.Assert.*;

/** Loopback-only TLS exercises; no installed service, discovery, clipboard or device is contacted. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=34,manifest=Config.NONE,application=Application.class)
public class HubAddressBindingTest {
    private static final String ID="11111111-2222-3333-4444-555555555555";
    private static final class Identity {
        final X509Certificate certificate;
        final KeyPair key;
        final String pin;
        Identity(boolean expired) throws Exception {
            KeyPairGenerator generator=KeyPairGenerator.getInstance("RSA"); generator.initialize(2048); key=generator.generateKeyPair();
            X500Name name=new X500Name("CN=StreamHub loopback test");long now=System.currentTimeMillis();
            certificate=new JcaX509CertificateConverter().getCertificate(new JcaX509v3CertificateBuilder(name,BigInteger.valueOf(now),
                    new Date(now-3600000),new Date(now+(expired?-60000:3600000)),name,key.getPublic())
                    .build(new JcaContentSignerBuilder("SHA256withRSA").build(key.getPrivate())));
            pin=HubControlClient.hex(MessageDigest.getInstance("SHA-256").digest(certificate.getEncoded()));
        }
        JSONObject json() throws Exception {return new JSONObject().put("v",1).put("id",ID).put("name","中文电脑")
                .put("pin",pin).put("port",49199);}
        SSLServerSocket server() throws Exception {
            KeyStore store=KeyStore.getInstance(KeyStore.getDefaultType());store.load(null,null);
            store.setKeyEntry("local",key.getPrivate(),"fixture".toCharArray(),new java.security.cert.Certificate[]{certificate});
            KeyManagerFactory keys=KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());keys.init(store,"fixture".toCharArray());
            SSLContext tls=SSLContext.getInstance("TLS");tls.init(keys.getKeyManagers(),null,new SecureRandom());
            SSLServerSocket socket=(SSLServerSocket)tls.getServerSocketFactory().createServerSocket(0,1,InetAddress.getByName("127.0.0.1"));
            socket.setSoTimeout(6000); return socket;
        }
    }
    private void reject(Throwing action) throws Exception {try{action.run();fail("Expected rejection");}catch(IOException|CertificateException expected){}}
    private interface Throwing {void run() throws Exception;}
    @Test public void addressEntryRejectsPublicOverlayAndUrlBeforeNetwork() throws Exception {
        for(String address:new String[]{"8.8.8.8","100.89.1.2","127.0.0.1","https://192.168.1.2","192.168.1.2:49199"})
            reject(()->HubLocalBinding.lookup(address));
    }
    @Test public void certificateChangeAndExpiredCertificateCannotAcquireTrust() throws Exception {
        Identity first=new Identity(false), second=new Identity(false);
        HubLocalBinding.PinTrust trust=new HubLocalBinding.PinTrust(null);
        trust.checkServerTrusted(new X509Certificate[]{first.certificate},"RSA");assertEquals(first.pin,trust.pin);
        reject(()->trust.checkServerTrusted(new X509Certificate[]{second.certificate},"RSA"));
        HubLocalBinding.PinTrust pinned=new HubLocalBinding.PinTrust(first.pin);
        reject(()->pinned.checkServerTrusted(new X509Certificate[]{second.certificate},"RSA"));
        reject(()->new HubLocalBinding.PinTrust(null).checkServerTrusted(new X509Certificate[]{new Identity(true).certificate},"RSA"));
    }
    @Test public void identityMustMatchCertificateAndControlPort() throws Exception {
        Identity first=new Identity(false);
        HubLanDiscovery.Device device=HubLocalBinding.identity("192.168.1.2",first.json(),first.pin);
        assertEquals(ID,device.id);assertEquals("中文电脑",device.name);assertEquals(first.pin,device.pin);
        reject(()->HubLocalBinding.identity("192.168.1.2",first.json().put("pin",new String(new char[64]).replace('\0','A')),first.pin));
        reject(()->HubLocalBinding.identity("192.168.1.2",first.json().put("port",443),first.pin));
        reject(()->HubLocalBinding.identity("192.168.1.2",first.json().put("id","wrong"),first.pin));
        reject(()->HubLocalBinding.identity("192.168.1.2",first.json().put("name","bad\nname"),first.pin));
    }
    private HubLanDiscovery.Device exchange(int status,String headers,String body,boolean success) throws Exception {
        Identity identity=new Identity(false);
        if(body==null)body=new JSONObject().put("success",true).put("result",identity.json()).toString();
        byte[] bytes=body.getBytes(StandardCharsets.UTF_8);String extra=headers;
        ExecutorService executor=Executors.newSingleThreadExecutor();
        try(SSLServerSocket listener=identity.server()) {
            Future<String> request=executor.submit(()->{
                try(SSLSocket socket=(SSLSocket)listener.accept()){
                    socket.setSoTimeout(6000);BufferedReader input=new BufferedReader(new InputStreamReader(socket.getInputStream(),StandardCharsets.US_ASCII));
                    StringBuilder seen=new StringBuilder();String line;
                    while((line=input.readLine())!=null&&!line.isEmpty())seen.append(line).append('\n');
                    OutputStream output=socket.getOutputStream();
                    output.write(("HTTP/1.1 "+status+" Fixture\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: "+bytes.length+"\r\n"+extra+"\r\n").getBytes(StandardCharsets.US_ASCII));
                    output.write(bytes);output.flush();return seen.toString();
                }
            });
            HubLanDiscovery.Device result=null;
            try {result=HubLocalBinding.lookupAt("127.0.0.1",listener.getLocalPort());if(!success)fail("Expected failure");}
            catch(IOException error){if(success)throw error;}
            String sent=request.get(8,TimeUnit.SECONDS);
            assertTrue(sent.startsWith("GET /v1/binding/identity HTTP/1.1"));
            assertFalse(sent.toLowerCase(Locale.ROOT).contains("authorization:"));
            assertFalse(sent.toLowerCase(Locale.ROOT).contains("cookie:"));
            return result;
        } finally {executor.shutdownNow();}
    }
    @Test public void directHttpsWorksWithoutDnsOrUdpAndSendsNoBindingToken() throws Exception {
        assertEquals("中文电脑",exchange(200,"",null,true).name);
    }
    @Test public void redirectIsNotFollowed() throws Exception {
        exchange(302,"Location: https://127.0.0.1:1/steal\r\n","{}",false);
    }
    @Test public void ownerMustOpenCodeWindowAndResponseIsBounded() throws Exception {
        exchange(409,"","{\"success\":false,\"message\":\"先生成短码\"}",false);
        exchange(200,"",new String(new char[65537]).replace('\0','x'),false);
    }
}
