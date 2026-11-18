package com.limelight.streamhub;

import android.content.Context;
import android.content.SharedPreferences;

/** Both entry points use one preference; it changes local controls, never stream parameters. */
final class HubPanelLayout {
    static final String SIDE="side", BARS="bars", FLOAT="float";
    static final String[] VALUES={SIDE,BARS,FLOAT};
    static final String[] LABELS={"侧边展开","黑边展开","自由浮动"};
    private HubPanelLayout(){}
    static SharedPreferences prefs(Context context){return context.getSharedPreferences("streamhub_controls",Context.MODE_PRIVATE);}
    static String mode(Context context){
        String value=prefs(context).getString("panel_layout",BARS);
        for(String valid:VALUES)if(valid.equals(value))return valid;
        return BARS;
    }
    static String label(Context context){for(int i=0;i<VALUES.length;i++)if(VALUES[i].equals(mode(context)))return LABELS[i];return LABELS[1];}
    static void show(android.app.Activity activity,Runnable changed){
        show(activity,false,changed);
    }
    static void show(android.app.Activity activity,boolean streaming,Runnable changed){
        int selected=java.util.Arrays.asList(VALUES).indexOf(mode(activity));
        (streaming?HubStreamUi.dialog(activity):HubStreamUi.hubDialog(activity)).setTitle("面板展开方式")
            .setSingleChoiceItems(new String[]{"侧边展开 · 横屏时向左或右展开","黑边展开 · 优先利用画面上下留白","自由浮动 · 拖动并记住面板位置"},selected,(d,w)->{
                String previous=mode(activity);
                if(prefs(activity).edit().putString("panel_layout",VALUES[w]).commit()){d.dismiss();changed.run();}
                else {prefs(activity).edit().putString("panel_layout",previous).commit();
                    ((android.app.AlertDialog)d).getListView().setItemChecked(selected,true);
                    android.widget.Toast.makeText(activity,"展开方式未保存，请重试",android.widget.Toast.LENGTH_LONG).show();}
            }).setNegativeButton("取消",null).show();
    }
}
