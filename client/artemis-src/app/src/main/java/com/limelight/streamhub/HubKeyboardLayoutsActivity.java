package com.limelight.streamhub;

import android.app.AlertDialog;
import android.content.*;
import android.os.Bundle;
import android.net.Uri;
import android.widget.*;
import androidx.preference.PreferenceManager;
import com.limelight.R;
import com.limelight.binding.input.virtual_controller.keyboard.KeyBoardControllerConfigurationLoader;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** SAF imports/exports only a selected keyboard layout, never application/binding preferences. */
public final class HubKeyboardLayoutsActivity extends HubSecondaryActivity {
    private static final int IMPORT=361,EXPORT=362,IMPORT_SHORTCUTS=363,EXPORT_SHORTCUTS=364;
    private TextView selected;
    private String pendingLayout,pendingExport;
    @Override protected String pageTitle(){return "屏幕键盘布局";}
    private String layout(){return PreferenceManager.getDefaultSharedPreferences(this).getString(
            KeyBoardControllerConfigurationLoader.OSC_PREFERENCE,KeyBoardControllerConfigurationLoader.OSC_PREFERENCE_VALUE);}
    @Override public void onCreate(Bundle state){
        super.onCreate(state);
        if(state!=null){pendingLayout=state.getString("layout");pendingExport=state.getString("export");}
        content.addView(text("完整屏幕键盘可在串流控制面板中打开；点完整键盘的配置图标，可依次启用、移动和缩放按键，再次点配置图标保存。布局选择与导入在下次打开键盘时生效。",14,true));
        LinearLayout box=card(content,"当前布局");selected=text("",16,false);box.addView(selected);refresh();
        action(box,"选择布局",false,()->{
            String[] names=names(),values=getResources().getStringArray(R.array.keyboard_axi_values);
            int index=Arrays.asList(values).indexOf(layout());
            HubStreamUi.hubDialog(this).setTitle("屏幕键盘布局").setSingleChoiceItems(names,index,(d,w)->{
                SharedPreferences prefs=PreferenceManager.getDefaultSharedPreferences(this);String old=layout();
                if(prefs.edit().putString(KeyBoardControllerConfigurationLoader.OSC_PREFERENCE,values[w]).commit()){refresh();d.dismiss();}
                else {prefs.edit().putString(KeyBoardControllerConfigurationLoader.OSC_PREFERENCE,old).commit();message("布局未保存，请重试。");}
            }).setNegativeButton("取消",null).show();
        });
        action(box,"导入布局文件",false,()->{
            pendingLayout=layout();Intent intent=new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/json");
            choose(intent,IMPORT);
        });
        action(box,"导出当前布局",false,()->{
            try {pendingExport=exportJson(getSharedPreferences(layout(),MODE_PRIVATE));
                Intent intent=new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/json");
                intent.putExtra(Intent.EXTRA_TITLE,"我控制-键盘布局.json");choose(intent,EXPORT);
            }catch(Exception error){message("无法导出布局："+error.getMessage());}
        });
        LinearLayout shortcuts=card(content,"自定义快捷键");
        shortcuts.addView(text("兼容原有JSON快捷键文件；导入后显示在串流面板的快捷键页，点按发送并释放按键。",14,true));
        action(shortcuts,"导入快捷键文件",false,()->choose(new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/json"),IMPORT_SHORTCUTS));
        action(shortcuts,"导出快捷键文件",false,()->{
            pendingExport=getSharedPreferences(HubShortcutBindings.PREFS,MODE_PRIVATE).getString(HubShortcutBindings.KEY,"{\"data\":[]}");
            Intent intent=new Intent(Intent.ACTION_CREATE_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("application/json");
            intent.putExtra(Intent.EXTRA_TITLE,"我控制-快捷键.json");choose(intent,EXPORT_SHORTCUTS);
        });
        LinearLayout danger=card(content,"");
        Button reset=action(danger,"还原当前布局",false,()->HubStreamUi.hubDialog(this).setTitle("还原当前键盘布局？")
            .setMessage("只清除当前布局的自定义位置和大小，其余布局及电脑绑定保留。")
            .setNegativeButton("取消",null).setPositiveButton("还原",(d,w)->replace(layout(),Collections.emptyMap())).show()
            .getButton(AlertDialog.BUTTON_POSITIVE).setTextColor(HubTheme.danger(this)));
        reset.setTextColor(HubTheme.danger(this));
    }
    private String[] names(){return new String[]{"布局 1","布局 2","布局 3","布局 4","布局 5"};}
    private void refresh(){String value=layout();String[] values=getResources().getStringArray(R.array.keyboard_axi_values),names=names();
        int index=Arrays.asList(values).indexOf(value);selected.setText(index>=0?names[index]:value);}
    private void choose(Intent intent,int request){try{startActivityForResult(intent,request);}catch(ActivityNotFoundException error){message("系统文件选择器不可用。");}}
    @Override protected void onSaveInstanceState(Bundle state){state.putString("layout",pendingLayout);state.putString("export",pendingExport);super.onSaveInstanceState(state);}
    @Override protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);
        if(result!=RESULT_OK||data==null||data.getData()==null)return;Uri uri=data.getData();
        try {
            if(request==EXPORT||request==EXPORT_SHORTCUTS){
                if(pendingExport==null)throw new IOException("导出内容已失效，请重新选择。");
                try(OutputStream output=getContentResolver().openOutputStream(uri,"wt")){
                    if(output==null)throw new IOException("无法写入所选文件");output.write(pendingExport.getBytes(StandardCharsets.UTF_8));
                }pendingExport=null;message(request==EXPORT?"键盘布局已导出。":"快捷键已导出。");
            }else if(request==IMPORT_SHORTCUTS){
                String json;try(InputStream input=getContentResolver().openInputStream(uri)){json=readBounded(input);}
                int count=HubShortcutBindings.parse(json).size();
                HubStreamUi.hubDialog(this).setTitle("导入自定义快捷键？").setMessage("将替换已导入的快捷键，共"+count+"项；内置快捷键和键盘布局保留。")
                    .setNegativeButton("取消",null).setPositiveButton("导入",(d,w)->{
                        SharedPreferences prefs=getSharedPreferences(HubShortcutBindings.PREFS,MODE_PRIVATE);String old=prefs.getString(HubShortcutBindings.KEY,null);
                        if(prefs.edit().putString(HubShortcutBindings.KEY,json).commit())message("已导入快捷键，下次展开串流面板时显示。");
                        else {prefs.edit().putString(HubShortcutBindings.KEY,old).commit();message("快捷键未保存，请重试。");}
                    }).show();
            }else if(request==IMPORT){
                if(pendingLayout==null)throw new IOException("导入目标已失效，请重新选择。");
                String name=pendingLayout;Map<String,String> entries;
                try(InputStream input=getContentResolver().openInputStream(uri)){entries=parse(input);}
                HubStreamUi.hubDialog(this).setTitle("导入到所选键盘布局？").setMessage("将替换所选布局的 "+entries.size()+" 项自定义设置，其他布局和电脑绑定保留。")
                    .setNegativeButton("取消",null).setPositiveButton("导入",(d,w)->replace(name,entries)).show();pendingLayout=null;
            }
        }catch(Exception error){message("布局操作未完成："+error.getMessage());}
    }
    static Map<String,String> parse(InputStream input)throws Exception{
        JSONObject object=new JSONObject(readBounded(input));if(object.length()>512)throw new JSONException("布局项目过多");
        Map<String,String> entries=new LinkedHashMap<>();Iterator<String> keys=object.keys();
        while(keys.hasNext()){String key=keys.next();Object value=object.get(key);
            if(key.isEmpty()||key.length()>256||!(value instanceof String))throw new JSONException("布局项目格式无效");
            new JSONObject((String)value);entries.put(key,(String)value);
        }return entries;
    }
    private static String readBounded(InputStream input)throws IOException{
        if(input==null)throw new IOException("无法读取所选文件");ByteArrayOutputStream bytes=new ByteArrayOutputStream();byte[] buffer=new byte[4096];int size;
        while((size=input.read(buffer))!=-1){if(bytes.size()+size>262144)throw new IOException("文件超过256KB");bytes.write(buffer,0,size);}
        return bytes.toString(StandardCharsets.UTF_8.name());
    }
    static String exportJson(SharedPreferences prefs)throws JSONException{
        JSONObject object=new JSONObject();for(Map.Entry<String,?> entry:prefs.getAll().entrySet())object.put(entry.getKey(),entry.getValue());return object.toString(2);
    }
    private void replace(String name,Map<String,String> entries){
        SharedPreferences prefs=getSharedPreferences(name,MODE_PRIVATE);Map<String,?> previous=prefs.getAll();SharedPreferences.Editor editor=prefs.edit().clear();
        for(Map.Entry<String,String> entry:entries.entrySet())editor.putString(entry.getKey(),entry.getValue());
        if(editor.commit()){message("已保存键盘布局，下次打开完整键盘时生效。");refresh();return;}
        SharedPreferences.Editor rollback=prefs.edit().clear();for(Map.Entry<String,?> item:previous.entrySet()){
            Object value=item.getValue();if(value instanceof String)rollback.putString(item.getKey(),(String)value);
            else if(value instanceof Boolean)rollback.putBoolean(item.getKey(),(Boolean)value);
            else if(value instanceof Integer)rollback.putInt(item.getKey(),(Integer)value);
            else if(value instanceof Float)rollback.putFloat(item.getKey(),(Float)value);
            else if(value instanceof Long)rollback.putLong(item.getKey(),(Long)value);
            else if(value instanceof Set)rollback.putStringSet(item.getKey(),(Set<String>)value);
        }rollback.commit();message("布局未保存，已尝试还原原有布局，请重试。");
    }
}
