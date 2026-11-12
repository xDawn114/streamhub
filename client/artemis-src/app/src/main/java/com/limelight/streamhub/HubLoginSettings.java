package com.limelight.streamhub;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.text.InputFilter;
import android.text.InputType;
import android.view.View;
import android.view.WindowManager;
import android.widget.*;

/** Visible from both connection settings and the live toolbar. */
public final class HubLoginSettings {
    private HubLoginSettings() { }
    public static LinearLayout view(Activity activity,boolean streaming,Runnable lockNow) {
        Context themed=streaming?HubStreamUi.context(activity):HubStreamUi.hubContext(activity);
        int text=streaming?HubStreamUi.TEXT:HubTheme.text(activity);
        int muted=streaming?HubStreamUi.MUTED:HubTheme.muted(activity);
        LinearLayout content=new LinearLayout(themed);content.setOrientation(LinearLayout.VERTICAL);
        HubLoginSecrets secrets=new HubLoginSecrets(activity);
        android.content.SharedPreferences controls=activity.getSharedPreferences("streamhub_controls",Context.MODE_PRIVATE);
        Switch lock=new Switch(themed);lock.setText("退出远控后自动锁定电脑");lock.setTextColor(text);lock.setTextSize(16);
        lock.setMinHeight(dp(activity,56));lock.setChecked(controls.getBoolean("lock_on_exit",false));
        lock.setOnCheckedChangeListener((v,enabled)->controls.edit().putBoolean("lock_on_exit",enabled).apply());
        content.addView(lock,new LinearLayout.LayoutParams(-1,-2));
        TextView lockHint=new TextView(themed);lockHint.setText("主动断开或返回退出时锁屏；旋转、调整分辨率和内部重连不会锁屏。");
        lockHint.setTextColor(muted);lockHint.setTextSize(13);content.addView(lockHint);
        Switch automatic=new Switch(themed);automatic.setText("锁屏密码自动填入");automatic.setTextColor(text);automatic.setTextSize(16);
        automatic.setMinHeight(dp(activity,56));automatic.setChecked(secrets.enabled());
        TextView hint=new TextView(themed);hint.setTextColor(muted);hint.setTextSize(13);
        Runnable refresh=()->hint.setText(secrets.saved()?"密码已加密保存在本机，仅用于当前绑定电脑。点击 Windows 密码框后自动填写。":"尚未保存。仅支持 Windows 密码，不支持 PIN、指纹、Hello 或 UAC。");
        refresh.run();
        automatic.setOnCheckedChangeListener((v,enabled)->{
            if(enabled && !secrets.saved()) {
                automatic.setChecked(false);
                editPassword(activity,streaming,()->{automatic.setChecked(secrets.enabled());refresh.run();});
            } else secrets.setEnabled(enabled);
        });
        content.addView(automatic,new LinearLayout.LayoutParams(-1,-2));content.addView(hint);
        LinearLayout actions=new LinearLayout(themed);actions.setOrientation(LinearLayout.HORIZONTAL);
        Button edit=new Button(themed);edit.setText("设置密码");styleAction(activity,edit);
        edit.setOnClickListener(v->editPassword(activity,streaming,()->{automatic.setChecked(secrets.enabled());refresh.run();}));
        Button forget=new Button(themed);forget.setText("忘记密码");styleAction(activity,forget);
        forget.setOnClickListener(v->{secrets.forget();automatic.setChecked(false);refresh.run();Toast.makeText(activity,"本机保存的密码已删除",Toast.LENGTH_SHORT).show();});
        actions.addView(edit,new LinearLayout.LayoutParams(0,-2,1));
        LinearLayout.LayoutParams second=new LinearLayout.LayoutParams(0,-2,1);second.leftMargin=dp(activity,8);actions.addView(forget,second);
        LinearLayout.LayoutParams spacing=new LinearLayout.LayoutParams(-1,-2);spacing.topMargin=dp(activity,12);content.addView(actions,spacing);
        if(lockNow!=null){Button now=new Button(themed);now.setText("立即锁定电脑");styleAction(activity,now);now.setOnClickListener(v->lockNow.run());
            LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(-1,-2);params.topMargin=dp(activity,8);content.addView(now,params);}
        return content;
    }
    private static void editPassword(Activity activity,boolean streaming,Runnable saved) {
        if(!new HubStore(activity).bound()){Toast.makeText(activity,"请先绑定电脑",Toast.LENGTH_LONG).show();return;}
        Context context=streaming?HubStreamUi.context(activity):HubStreamUi.hubContext(activity);
        int color=streaming?HubStreamUi.TEXT:HubTheme.text(activity);
        LinearLayout content=new LinearLayout(context);content.setOrientation(LinearLayout.VERTICAL);content.setPadding(dp(activity,24),dp(activity,8),dp(activity,24),dp(activity,8));
        TextView note=new TextView(context);note.setText("只保存当前电脑的 Windows 密码。密码在本机加密，不进入剪贴板、日志或云备份。请保持本机屏幕锁已启用。\n连接锁屏后，点选电脑的密码框即可自动填入；切换用户、PIN/Hello 和 UAC 请手动处理。");note.setTextColor(color);note.setTextSize(14);content.addView(note);
        EditText password=new EditText(context);password.setHint("Windows 密码");password.setTextColor(color);
        password.setInputType(InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_PASSWORD|InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        password.setSingleLine(true);password.setFilters(new InputFilter[]{new InputFilter.LengthFilter(256)});
        password.setImeOptions(android.view.inputmethod.EditorInfo.IME_FLAG_NO_PERSONALIZED_LEARNING |
            android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI | android.view.inputmethod.EditorInfo.IME_ACTION_DONE);
        password.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO_EXCLUDE_DESCENDANTS);
        LinearLayout.LayoutParams field=new LinearLayout.LayoutParams(-1,dp(activity,56));field.topMargin=dp(activity,12);content.addView(password,field);
        CheckBox submit=new CheckBox(context);submit.setText("填入后自动按 Enter 确认登录");submit.setTextColor(color);submit.setMinHeight(dp(activity,48));submit.setChecked(false);content.addView(submit);
        ScrollView scroll=new ScrollView(context);scroll.setFillViewport(false);scroll.addView(content);
        AlertDialog dialog=(streaming?HubStreamUi.dialog(activity):HubStreamUi.hubDialog(activity)).setTitle("当前电脑的自动填写密码")
            .setView(scroll).setPositiveButton("加密保存",null).setNegativeButton("取消",null).create();
        dialog.getWindow().addFlags(WindowManager.LayoutParams.FLAG_SECURE);
        dialog.getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        dialog.setOnShowListener(ignored->{
            dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v->{
                try{new HubLoginSecrets(activity).save(password.getText().toString(),submit.isChecked());password.getText().clear();dialog.dismiss();saved.run();}
                catch(Exception error){password.setError(error.getMessage()==null?"密码未保存，请重试":error.getMessage());}
            });
        });
        dialog.setOnDismissListener(ignored->password.getText().clear());dialog.show();
    }
    private static void styleAction(Context context,Button button){button.setMinHeight(dp(context,48));button.setTextSize(14);button.setAllCaps(false);button.setPadding(dp(context,12),dp(context,8),dp(context,12),dp(context,8));}
    private static int dp(Context context,int value){return Math.round(context.getResources().getDisplayMetrics().density*value);}
}
