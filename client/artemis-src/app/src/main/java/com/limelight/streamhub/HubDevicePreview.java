package com.limelight.streamhub;

import android.content.Context;
import android.graphics.*;
import android.os.*;
import android.util.AtomicFile;
import android.view.PixelCopy;
import android.view.SurfaceView;
import com.limelight.LimeLog;
import java.io.*;
import java.security.MessageDigest;

/** One bounded local preview per trusted computer; never a live or uploaded wallpaper. */
public final class HubDevicePreview {
    private HubDevicePreview(){}
    private static File file(Context context,String identity)throws Exception{
        byte[] hash=MessageDigest.getInstance("SHA-256").digest(identity.getBytes(java.nio.charset.StandardCharsets.UTF_8));
        StringBuilder name=new StringBuilder("hub-preview-");for(byte value:hash)name.append(String.format(java.util.Locale.ROOT,"%02x",value&255));
        return new File(context.getCacheDir(),name+".jpg");
    }
    public static Bitmap read(Context context,String identity){
        if(identity.isEmpty())return null;
        try {File file=file(context,identity);if(!file.isFile())return null;
            BitmapFactory.Options bounds=new BitmapFactory.Options();bounds.inJustDecodeBounds=true;BitmapFactory.decodeFile(file.getPath(),bounds);
            if(bounds.outWidth>640||bounds.outHeight>640||bounds.outWidth<1||bounds.outHeight<1)return null;
            return BitmapFactory.decodeFile(file.getPath());
        }catch(Exception error){LimeLog.warning("Device preview read: "+error);return null;}
    }
    public static void capture(Context context,SurfaceView surface,String identity){
        if(Build.VERSION.SDK_INT<24||identity.isEmpty()||!surface.getHolder().getSurface().isValid())return;
        Context app=context.getApplicationContext();
        HubStore trust=new HubStore(app);String revision=trust.get("bindingRevision","");
        if(!trust.bound()||!identity.equals(trust.get("deviceId","")))return;
        int width=surface.getWidth(),height=surface.getHeight();if(width<1||height<1)return;
        float ratio=Math.min(1f,640f/Math.max(width,height));
        Bitmap bitmap=Bitmap.createBitmap(Math.max(1,Math.round(width*ratio)),Math.max(1,Math.round(height*ratio)),Bitmap.Config.ARGB_8888);
        try {PixelCopy.request(surface,bitmap,result->{
            if(result!=PixelCopy.SUCCESS){bitmap.recycle();return;}
            new Thread(()->{
                AtomicFile output=null;FileOutputStream stream=null;
                try {
                    synchronized(HubStore.class){
                    HubStore current=new HubStore(app);
                    if(!current.bound()||!identity.equals(current.get("deviceId",""))||!revision.equals(current.get("bindingRevision","")))return;
                    output=new AtomicFile(file(app,identity));stream=output.startWrite();
                    if(!bitmap.compress(Bitmap.CompressFormat.JPEG,78,stream))throw new IOException("Preview encoding failed");
                    if(!identity.equals(new HubStore(app).get("deviceId",""))){output.failWrite(stream);stream=null;forget(app,identity);return;}
                    output.finishWrite(stream);stream=null;
                    }
                }catch(Exception error){if(output!=null&&stream!=null)output.failWrite(stream);LimeLog.warning("Device preview save: "+error);}
                finally{bitmap.recycle();}
            },"hub-device-preview").start();
        },new Handler(Looper.getMainLooper()));}catch(IllegalArgumentException error){bitmap.recycle();LimeLog.warning("Device preview capture: "+error);}
    }
    public static void forget(Context context,String identity){
        if(identity.isEmpty())return;try {new AtomicFile(file(context,identity)).delete();}catch(Exception error){LimeLog.warning("Device preview clear: "+error);}
    }
}
