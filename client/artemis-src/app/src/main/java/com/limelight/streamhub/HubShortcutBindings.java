package com.limelight.streamhub;

import org.json.*;
import java.util.*;

/** Validated legacy shortcut-file format; importing it never executes a key. */
final class HubShortcutBindings {
    static final String PREFS="specialPrefs",KEY="special_key";
    static final class Binding {
        final String name;final int[] keys;
        Binding(String name,int[] keys){this.name=name;this.keys=keys;}
    }
    static List<Binding> parse(String json)throws Exception{
        if(json.length()>262144)throw new JSONException("快捷键文件超过256KB");
        JSONArray data=new JSONObject(json).getJSONArray("data");
        if(data.length()>64)throw new JSONException("最多导入64个快捷键");
        List<Binding> bindings=new ArrayList<>();Set<String> names=new HashSet<>();
        for(int i=0;i<data.length();i++){
            JSONObject row=data.getJSONObject(i);String name=row.getString("name").trim();JSONArray codes=row.getJSONArray("keys");
            if(name.isEmpty()||name.length()>64||!names.add(name)||codes.length()<1||codes.length()>8)throw new JSONException("快捷键名称或按键数量无效");
            int[] keys=new int[codes.length()];
            for(int k=0;k<keys.length;k++){
                String code=codes.getString(k);
                if(code.matches("0[xX][0-9a-fA-F]{1,2}"))keys[k]=Integer.parseInt(code.substring(2),16);
                else if(code.matches("VK_[A-Z0-9_]+"))keys[k]=com.limelight.utils.KeyMapper.class.getField(code).getInt(null);
                else throw new JSONException("不支持的按键："+code);
                if(keys[k]<1||keys[k]>255)throw new JSONException("按键编码超出范围");
            }
            bindings.add(new Binding(name,keys));
        }return bindings;
    }
}
