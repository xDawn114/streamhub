package com.limelight.streamhub;

import android.app.KeyguardManager;
import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.security.MessageDigest;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/** Per-bound-PC opt-in. Only AES-GCM ciphertext leaves memory; Android backup is disabled. */
public final class HubLoginSecrets {
    interface Codec { String seal(String scope, String value) throws Exception; String open(String scope, String value) throws Exception; }
    private final Context context;
    private final SharedPreferences preferences;
    private final String scope;
    private final Codec codec;
    public HubLoginSecrets(Context context) {
        this(context, scope(context), new Codec() {
            public String seal(String scope,String value)throws Exception{return crypt(scope,value,true);}
            public String open(String scope,String value)throws Exception{return crypt(scope,value,false);}
        });
    }
    HubLoginSecrets(Context context,String scope,Codec codec) {
        this.context=context.getApplicationContext();this.scope=scope;this.codec=codec;
        preferences=context.getSharedPreferences("streamhub-login-secrets",Context.MODE_PRIVATE);
    }
    private static String scope(Context context) {
        try {
            HubStore store=new HubStore(context);
            if(!store.bound())return "";
            org.json.JSONObject invitation=new org.json.JSONObject(new String(Base64.decode(store.invitation(),Base64.URL_SAFE|Base64.NO_WRAP),StandardCharsets.UTF_8));
            String identity=store.get("deviceId","")+":"+invitation.getString("pin");
            return HubControlClient.hex(MessageDigest.getInstance("SHA-256").digest(identity.getBytes(StandardCharsets.UTF_8)));
        } catch(Exception ignored){return "";}
    }
    public boolean saved(){return !scope.isEmpty() && preferences.contains(scope+".secret");}
    public boolean enabled(){return saved() && preferences.getBoolean(scope+".enabled",false);}
    public boolean submit(){return enabled() && preferences.getBoolean(scope+".submit",false);}
    public boolean deviceSecure(){KeyguardManager manager=(KeyguardManager)context.getSystemService(Context.KEYGUARD_SERVICE);return manager!=null && manager.isDeviceSecure();}
    private boolean deviceLocked(){KeyguardManager manager=(KeyguardManager)context.getSystemService(Context.KEYGUARD_SERVICE);return manager==null || manager.isDeviceLocked();}
    public void save(String password,boolean submit)throws Exception {
        if(scope.isEmpty())throw new java.io.IOException("请先绑定电脑。");
        validate(password);
        if(!deviceSecure() || deviceLocked())throw new java.io.IOException("请先为本机设置锁屏密码，并保持本机解锁。");
        String sealed=codec.seal(scope,password);
        if(!preferences.edit().putString(scope+".secret",sealed).putBoolean(scope+".enabled",true).putBoolean(scope+".submit",submit).commit())
            throw new java.io.IOException("密码未保存，请重试。");
    }
    public void setEnabled(boolean enabled){if(saved())preferences.edit().putBoolean(scope+".enabled",enabled).apply();}
    public String password()throws Exception {
        if(!enabled() || deviceLocked())throw new java.io.IOException("密码未启用或本机已锁定。");
        String result=codec.open(scope,preferences.getString(scope+".secret",""));validate(result);return result;
    }
    public void forget(){if(!scope.isEmpty())preferences.edit().remove(scope+".secret").remove(scope+".enabled").remove(scope+".submit").commit();}
    static void validate(String value) {
        if(value==null || value.isEmpty() || value.length()>256)throw new IllegalArgumentException("请输入1至256个字符的 Windows 密码。");
        for(int i=0;i<value.length();i++)if(Character.isISOControl(value.charAt(i)))throw new IllegalArgumentException("密码不能包含控制字符。");
    }
    private static SecretKey key(String scope,boolean create)throws Exception {
        KeyStore store=KeyStore.getInstance("AndroidKeyStore");store.load(null);String alias="StreamHubLogin."+scope;
        if(!store.containsAlias(alias)) {
            if(!create)throw new java.io.IOException("本机密码密钥不可用，请重新保存。");
            KeyGenerator generator=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");
            KeyGenParameterSpec.Builder spec=new KeyGenParameterSpec.Builder(alias,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE);
            if(Build.VERSION.SDK_INT>=28)spec.setUnlockedDeviceRequired(true);
            generator.init(spec.build());generator.generateKey();
        }
        return (SecretKey)store.getKey(alias,null);
    }
    private static String crypt(String scope,String value,boolean seal)throws Exception {
        Cipher cipher=Cipher.getInstance("AES/GCM/NoPadding");byte[] bytes=null;
        try {
            if(seal) {
                cipher.init(Cipher.ENCRYPT_MODE,key(scope,true));cipher.updateAAD(scope.getBytes(StandardCharsets.US_ASCII));
                bytes=value.getBytes(StandardCharsets.UTF_8);
                return Base64.encodeToString(cipher.getIV(),Base64.NO_WRAP)+"."+Base64.encodeToString(cipher.doFinal(bytes),Base64.NO_WRAP);
            }
            String[] parts=value.split("\\.",-1);if(parts.length!=2)throw new java.io.IOException("本机密码记录无效，请重新保存。");
            cipher.init(Cipher.DECRYPT_MODE,key(scope,false),new GCMParameterSpec(128,Base64.decode(parts[0],Base64.NO_WRAP)));
            cipher.updateAAD(scope.getBytes(StandardCharsets.US_ASCII));bytes=cipher.doFinal(Base64.decode(parts[1],Base64.NO_WRAP));
            return new String(bytes,StandardCharsets.UTF_8);
        } finally {if(bytes!=null)java.util.Arrays.fill(bytes,(byte)0);}
    }
}
