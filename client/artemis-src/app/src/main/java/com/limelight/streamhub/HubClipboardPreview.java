package com.limelight.streamhub;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ClipboardManager;
import android.content.Context;
import android.graphics.BitmapFactory;
import android.os.Build;
import android.os.PersistableBundle;
import android.view.View;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.io.InputStream;

/** An explicit foreground result; no dependency on a vendor keyboard's clipboard history. */
public final class HubClipboardPreview {
    private HubClipboardPreview() { }
    public static void showFailure(Activity activity,String message,Runnable retry) {
        if(activity.isFinishing() || activity.isDestroyed())return;
        HubStreamUi.dialog(activity).setTitle("未能读取电脑剪贴板")
                .setMessage(message+"\n\n原平板剪贴板已保留。确认电脑已登录并解锁后，可以重试。")
                .setPositiveButton("重试",(d,w)->retry.run()).setNegativeButton("关闭",null).show();
    }
    public static void show(Activity activity,ClipData clip,boolean sensitive) {
        if(activity.isFinishing() || activity.isDestroyed())return;
        int pad=Math.round(20*activity.getResources().getDisplayMetrics().density);
        LinearLayout body=new LinearLayout(activity);body.setOrientation(LinearLayout.VERTICAL);body.setPadding(pad,pad,pad,pad);
        TextView state=new TextView(activity);state.setTextSize(14);state.setText("已收到电脑内容，正在写入平板剪贴板…");body.addView(state);
        state.setTextColor(HubStreamUi.MUTED);
        ClipData.Item item=clip.getItemAt(0);
        if(item.getUri()!=null) {
            ImageView image=new ImageView(activity);image.setAdjustViewBounds(true);image.setMaxHeight(pad*14);
            try(InputStream stream=activity.getContentResolver().openInputStream(item.getUri())) {
                BitmapFactory.Options options=new BitmapFactory.Options();options.inSampleSize=8;
                image.setImageBitmap(BitmapFactory.decodeStream(stream,null,options));
            } catch(Exception ignored){state.setText("已收到图片，可复制后粘贴到支持图片的应用。");}
            image.setContentDescription("收到的电脑剪贴板图片");body.addView(image);
        } else {
            TextView text=new TextView(activity);text.setTextSize(16);text.setTextIsSelectable(true);
            text.setTextColor(HubStreamUi.TEXT);
            CharSequence value=item.getText();int count=value==null?0:value.length();
            text.setText(value==null?"":value.subSequence(0,Math.min(count,16000)));body.addView(text);
            if(count>16000){TextView hint=new TextView(activity);hint.setTextColor(HubStreamUi.MUTED);hint.setText("预览仅显示前 16000 字，复制保留完整内容。");body.addView(hint);}
        }
        ScrollView scroll=new ScrollView(activity);scroll.addView(body);
        AlertDialog dialog=HubStreamUi.dialog(activity).setTitle("电脑剪贴板").setView(scroll)
                .setPositiveButton("再次复制",null).setNegativeButton("关闭",null).create();
        Runnable copy=()->{
            try {
                if(Build.VERSION.SDK_INT>=24){PersistableBundle extras=new PersistableBundle();
                    extras.putBoolean(com.limelight.Game.CLIPBOARD_IDENTIFIER,true);extras.putBoolean("android.content.extra.IS_SENSITIVE",sensitive);clip.getDescription().setExtras(extras);}
                ClipboardManager manager=(ClipboardManager)activity.getSystemService(Context.CLIPBOARD_SERVICE);
                manager.setPrimaryClip(clip);
                ClipData read=manager.getPrimaryClip();
                boolean same=read!=null && read.getItemCount()>0 &&
                        (item.getUri()!=null?item.getUri().equals(read.getItemAt(0).getUri()):
                                android.text.TextUtils.equals(item.getText(),read.getItemAt(0).getText()));
                state.setText(same?"已写入平板剪贴板，可到其他应用粘贴。":"已收到内容，但系统未确认写入；请点再次复制。");
            } catch(RuntimeException error){state.setText("写入未完成，请点再次复制或选择下方文字。");}
        };
        dialog.setOnShowListener(d->{dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v->copy.run());
            dialog.getWindow().getDecorView().post(copy);});dialog.show();
    }
}
