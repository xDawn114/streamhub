package com.limelight.streamhub;

import android.app.Activity;
import android.content.*;
import android.database.Cursor;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.net.Uri;
import android.os.*;
import android.provider.OpenableColumns;
import android.view.View;
import android.widget.*;
import org.json.*;
import java.util.concurrent.*;

/** Native document pickers, visible progress, no all-files storage permission. */
public final class HubFilesActivity extends Activity {
    /** Top-level destinations switch in place, including this separate Activity. */
    public static void open(Activity source) {
        source.startActivity(new Intent(source,HubFilesActivity.class), HubNavigation.transition(source));
        HubNavigation.holdSurface(source);
    }
    private static final int OPEN_FILE=861, OPEN_FOLDER=862;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private HubStore store; private HubFileTasks tasks;
    private Button pauseButton;
    private Button refreshButton, sendButton, clearButton;
    private TextView state, browseLocation; private LinearLayout localList, remoteList, browseList;
    private String browsePath = "", renderedBrowse = "", browseSort="name";
    private int browseOffset;
    private String browseRootId="", pendingRootId="", pendingComputer="", renderedRoots="";
    private Button computerTab,tasksTab,sortButton;
    private boolean showingTasks;
    private LinearLayout rootsList,computerPane,transferPane;
    private Button downloadSelected;
    private JSONArray knownRoots=new JSONArray();
    private boolean uploadAllowed=true,deleteAllowed;
    private final java.util.LinkedHashMap<String,JSONObject> selectedFiles=new java.util.LinkedHashMap<>();
    private JSONArray pendingBrowseDownloads=new JSONArray();
    private volatile HubFileClient control;
    private JSONObject chosenDownload;
    private JSONArray remoteTasks = new JSONArray();
    private boolean foreground;
    private boolean fileTransferAllowed;
    private volatile boolean checking;
    private long nextRemoteRefreshAt;
    private String renderedTasks = "";
    private int background, surface, field, border, textColor, muted, accent, primary, onAccent, ripple;
    private final Runnable refresh = new Runnable() { @Override public void run() { if (foreground) { renderLocal(); if (!checking && SystemClock.elapsedRealtime() >= nextRemoteRefreshAt) loadRemote(); main.postDelayed(this, 1000); } } };
    @Override public void onCreate(Bundle saved) {
        super.onCreate(saved); HubNavigation.prepareWindow(this); store=new HubStore(this); tasks=new HubFileTasks(this);
        if(saved!=null)try{String value=saved.getString("download", "");if(!value.isEmpty())chosenDownload=new JSONObject(value);}catch(Exception ignored){}
        if(saved!=null) { browsePath=saved.getString("browsePath", ""); browseOffset=saved.getInt("browseOffset", 0); browseRootId=saved.getString("browseRootId","");pendingRootId=saved.getString("pendingRootId","");pendingComputer=saved.getString("pendingComputer","");showingTasks=saved.getBoolean("showingTasks",false);
            browseSort=saved.getString("browseSort","name");
            try{JSONArray selected=new JSONArray(saved.getString("selectedFiles","[]"));for(int i=0;i<selected.length();i++){JSONObject entry=selected.getJSONObject(i);selectedFiles.put(entry.getString("path"),entry);}}catch(Exception ignored){}
            try{pendingBrowseDownloads=new JSONArray(saved.getString("pendingBrowseDownloads","[]"));}catch(Exception ignored){} }
        refreshPalette();
        getWindow().setBackgroundDrawable(new android.graphics.drawable.ColorDrawable(background));
        getWindow().getDecorView().setBackgroundColor(background);
        ScrollView scroll=new ScrollView(this); scroll.setFillViewport(true); scroll.setBackgroundColor(background);
        FrameLayout centered=new FrameLayout(this); scroll.addView(centered);
        HubWindowLayout layout=HubWindowLayout.of(this);
        LinearLayout content=new LinearLayout(this); content.setOrientation(LinearLayout.VERTICAL); content.setPadding(dp(layout.pageInsetDp()),dp(layout.compact()?16:24),dp(layout.pageInsetDp()),dp(30));
        centered.addView(content,new FrameLayout.LayoutParams(dp(layout.contentWidthDp()),-2,android.view.Gravity.TOP|android.view.Gravity.CENTER_HORIZONTAL));
        HubNavigation navigation=new HubNavigation(this,scroll,3,index->{
            if(index==3)return;
            startActivity(new Intent(this,HubActivity.class).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP|Intent.FLAG_ACTIVITY_SINGLE_TOP).putExtra("HubPage",index), HubNavigation.transition(this));
            // CLEAR_TOP closes this Activity after the hub is ready. Do not
            // separately finish and discard its surface before that handoff.
            HubNavigation.holdSurface(this);
        }); setContentView(navigation.view);
        LinearLayout toolbar=new LinearLayout(this); toolbar.setGravity(android.view.Gravity.CENTER_VERTICAL);
        Button back=new Button(this); back.setText("‹"); back.setTextSize(32); back.setTextColor(accent); back.setAllCaps(false); back.setContentDescription("返回设备");
        back.setMinHeight(dp(48)); back.setMinWidth(dp(48)); back.setPadding(0,0,0,dp(4)); back.setBackgroundColor(android.graphics.Color.TRANSPARENT); back.setOnClickListener(v->finish());
        toolbar.addView(back,new LinearLayout.LayoutParams(dp(48),dp(56)));
        TextView title=text("远程文件",layout.compact()?24:28); title.setTypeface(null,Typeface.BOLD); toolbar.addView(title,new LinearLayout.LayoutParams(0,-2,1));
        toolbar.setMinimumHeight(dp(60));content.addView(toolbar,new LinearLayout.LayoutParams(-1,-2));
        LinearLayout statusBar=new LinearLayout(this); statusBar.setMinimumHeight(dp(52)); statusBar.setPadding(dp(12),dp(8),dp(12),dp(8));
        GradientDrawable statusBackground=new GradientDrawable(); statusBackground.setColor(field); statusBackground.setCornerRadius(dp(8)); statusBackground.setStroke(dp(1),border); statusBar.setBackground(statusBackground);
        state=text("正在读取文件授权…",15); state.setTextColor(muted); state.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE); statusBar.addView(state,new LinearLayout.LayoutParams(-1,-2)); LinearLayout.LayoutParams statusSize=new LinearLayout.LayoutParams(-1,-2); statusSize.topMargin=dp(4); statusSize.bottomMargin=dp(4); content.addView(statusBar,statusSize);
        content.addView(text(layout.compact()?"无需启动远控；上传和删除单独授权。":"无需启动远控。在电脑一次允许此设备并选择浏览范围后，即可浏览并下载；上传和删除单独授权。",14));
        refreshButton=button(content,"申请授权",this::loadRemote);
        LinearLayout tabs=new LinearLayout(this);
        computerTab=button(tabs,"电脑文件",()->showFilePane(false));
        tasksTab=button(tabs,"传输任务",()->showFilePane(true));
        for(int i=0;i<tabs.getChildCount();i++){LinearLayout.LayoutParams tabSize=new LinearLayout.LayoutParams(0,-2,1);if(i>0)tabSize.leftMargin=dp(8);tabs.getChildAt(i).setLayoutParams(tabSize);}
        LinearLayout.LayoutParams tabsSize=new LinearLayout.LayoutParams(-1,-2);tabsSize.topMargin=dp(8);content.addView(tabs,tabsSize);
        computerPane=new LinearLayout(this);computerPane.setOrientation(LinearLayout.VERTICAL);content.addView(computerPane);
        rootsList=new LinearLayout(this);rootsList.setOrientation(LinearLayout.VERTICAL);computerPane.addView(rootsList);
        computerPane.addView(heading("电脑文件")); browseLocation=text("等待电脑授权",14); computerPane.addView(browseLocation);
        sortButton=button(computerPane,"排序："+sortLabel(),this::showSortChoices);
        browseList=new LinearLayout(this); browseList.setOrientation(LinearLayout.VERTICAL); computerPane.addView(browseList);
        downloadSelected=button(computerPane,"下载所选文件",true,this::chooseBrowseDestination);
        transferPane=new LinearLayout(this);transferPane.setOrientation(LinearLayout.VERTICAL);transferPane.setVisibility(View.GONE);content.addView(transferPane);
        sendButton=button(transferPane,"选择文件发到电脑",true,()->{
            if (!fileTransferAllowed) { state.setText("请先申请授权，等待电脑允许后再发送文件。"); return; }
            pendingComputer=store.get("deviceId","");
            Intent pick=new Intent(Intent.ACTION_OPEN_DOCUMENT).putExtra(Intent.EXTRA_ALLOW_MULTIPLE,true).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION); startActivityForResult(pick,OPEN_FILE);
        });
        pauseButton=button(transferPane,"暂停当前传输",()->{ HubFileService.pause(); state.setText("正在暂停，已确认进度会保留。"); });
        transferPane.addView(heading("电脑发来的文件")); remoteList=new LinearLayout(this); remoteList.setOrientation(LinearLayout.VERTICAL); transferPane.addView(remoteList);
        transferPane.addView(heading("本机传输任务")); localList=new LinearLayout(this); localList.setOrientation(LinearLayout.VERTICAL); transferPane.addView(localList);
        clearButton=button(transferPane,"清理已完成/取消记录",()->{ tasks.clearFinished(); renderLocal(); });
        content.addView(text("单个文件最多2GiB。断网后可继续；保存到平板时自动添加文件后缀，同名文件不会覆盖。",14));
        showFilePane(showingTasks);renderControls();
        loadRemote();
    }
    @Override public void finish() { super.finish(); HubNavigation.holdSurface(this); }
    private int dp(int value) { return Math.round(value*getResources().getDisplayMetrics().density); }
    private void refreshPalette() {
        background=HubTheme.background(this); surface=HubTheme.surface(this); field=HubTheme.field(this); border=HubTheme.border(this);
        textColor=HubTheme.text(this); muted=HubTheme.muted(this); accent=HubTheme.accent(this); primary=HubTheme.primary(this); onAccent=HubTheme.onAccent(this); ripple=HubTheme.ripple(this);
        getWindow().setStatusBarColor(background); getWindow().setNavigationBarColor(background);
        int flags=HubTheme.isDark(this)?0:View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR;
        if(Build.VERSION.SDK_INT>=Build.VERSION_CODES.O&&!HubTheme.isDark(this))flags|=View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR;
        getWindow().getDecorView().setSystemUiVisibility(flags); HubStreamUi.keepColors(getWindow().getDecorView());
    }
    private TextView text(String value,int size) { TextView view=new TextView(this); view.setText(value); view.setTextColor(textColor); view.setTextSize(size); view.setPadding(0,dp(8),0,dp(8)); return view; }
    private TextView heading(String value) { TextView view=text(value,20); view.setTypeface(null,Typeface.BOLD); view.setPadding(0,dp(16),0,dp(8)); LinearLayout.LayoutParams spacing=new LinearLayout.LayoutParams(-1,-2); spacing.topMargin=dp(8); view.setLayoutParams(spacing); return view; }
    private Button button(LinearLayout parent,String label,Runnable action) { return button(parent,label,false,action); }
    private Button button(LinearLayout parent,String label,boolean primary,Runnable action) {
        Button button=new Button(this); button.setStateListAnimator(null); button.setElevation(0); button.setText(label); button.setTextSize(16); button.setTextColor(primary ? onAccent : accent); button.setAllCaps(false); button.setMinHeight(dp(52));button.setPadding(dp(12),dp(8),dp(12),dp(8));
        GradientDrawable shape=new GradientDrawable(); shape.setColor(primary ? this.primary : field); shape.setCornerRadius(dp(8)); shape.setStroke(dp(1),primary ? this.primary : border);
        button.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(ripple),shape,null));
        LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(-1,-2); params.topMargin=dp(8); parent.addView(button,params); button.setOnClickListener(v->action.run()); return button;
    }
    private void loadRemote() {
        if(!store.bound()) { state.setText("请先在“连接”页绑定电脑，即可独立访问文件。"); return; }
        if(checking) return; checking=true; state.setText("正在连接电脑文件接口…");
        final String requestedPath=browsePath; final int requestedOffset=browseOffset; final String requestedRoot=browseRootId;
        final String requestedSort=browseSort,requestedComputer=store.get("deviceId","");
        renderControls();
        nextRemoteRefreshAt=SystemClock.elapsedRealtime()+10000;
        worker.execute(()->{
            try {
                if(!requestedComputer.equals(store.get("deviceId","")))throw new java.io.IOException("电脑绑定已改变，请刷新。");
                HubFileClient client=new HubFileClient(this); control=client; client.connect(); JSONObject registration=client.register();
                boolean allowed=registration.optBoolean("allowed"), enabled=registration.optBoolean("enabled");
                JSONArray available=allowed && enabled ? client.json("tasks",null).optJSONArray("tasks") : new JSONArray();
                JSONObject listing=null; JSONArray roots=new JSONArray();
                if(allowed && enabled && client.supportsRoots())roots=client.json("roots",null).optJSONArray("roots");
                String resolvedRoot=requestedRoot;
                if(resolvedRoot.isEmpty() && roots!=null && roots.length()>0)resolvedRoot=roots.optJSONObject(0).optString("id");
                final String currentRoot=resolvedRoot;
                String browseError="等待电脑授权后可浏览文件。";
                if(allowed && enabled) {
                    if(client.supportsBrowse()) try { listing=client.json("browse",new JSONObject().put("path",requestedPath).put("offset",requestedOffset).put("rootId",currentRoot).put("sort",requestedSort)).optJSONObject("listing"); }
                    catch(Exception error) { browseError=error.getMessage(); }
                    else browseError="电脑端尚未支持共享目录浏览，请更新电脑端。";
                }
                final JSONArray resolvedRoots=roots;
                final JSONObject shared=listing; final String sharedMessage=browseError;
                String message="设备核对码："+registration.optString("code")+" · "+(allowed?(enabled?"已允许收发":"电脑文件功能尚未开启"):"等待电脑允许");
                main.post(()->{ if(isDestroyed()||!requestedComputer.equals(store.get("deviceId",""))) return; browseRootId=currentRoot;fileTransferAllowed=allowed && enabled; uploadAllowed=registration.optBoolean("uploadAllowed",true);deleteAllowed=registration.optBoolean("deleteAllowed",false);renderRoots(resolvedRoots); refreshButton.setText(fileTransferAllowed?"刷新电脑文件":"申请授权"); state.setText(message); remoteTasks=available; renderRemote(remoteTasks); renderBrowse(shared,sharedMessage); renderControls(); });
            }catch(Exception error) { main.post(()->{ if(!isDestroyed()) { fileTransferAllowed=false;deleteAllowed=false;renderRoots(new JSONArray());state.setText("未连接："+error.getMessage()); renderRemote(new JSONArray()); renderBrowse(null,"连接电脑后可浏览共享文件夹。"); renderControls(); } }); }
            finally { if(control!=null)control.cancel(); control=null; main.post(()->{checking=false;nextRemoteRefreshAt=SystemClock.elapsedRealtime()+10000;if(!isDestroyed())renderControls();}); }
        });
    }
    private void navigateBrowse(String path,int offset) {
        if(checking)return;
        selectedFiles.clear();updateSelection();browsePath=path; browseOffset=offset; renderedBrowse=""; nextRemoteRefreshAt=0; loadRemote();
    }
    private String sortLabel(){return "modified".equals(browseSort)?"最近修改":"size".equals(browseSort)?"文件从大到小":"name-desc".equals(browseSort)?"名称 Z–A":"名称 A–Z";}
    private void showSortChoices(){
        if(checking)return;
        String[] values={"name","name-desc","modified","size"};int selected=java.util.Arrays.asList(values).indexOf(browseSort);
        HubStreamUi.hubDialog(this).setTitle("电脑文件排序").setSingleChoiceItems(new String[]{"名称 A–Z","名称 Z–A","最近修改","文件从大到小"},Math.max(0,selected),(d,w)->{
            d.dismiss();browseSort=values[w];sortButton.setText("排序："+sortLabel());navigateBrowse(browsePath,0);
        }).setNegativeButton("取消",null).show();
    }
    private void renderBrowse(JSONObject listing,String error) {
        String signature=browseRootId+":"+deleteAllowed+":"+(listing==null?error:listing.toString());
        if(signature.equals(renderedBrowse))return; renderedBrowse=signature; browseList.removeAllViews();
        if(listing==null) { browseLocation.setText(error); if(!browsePath.isEmpty() || browseOffset>0) button(browseList,"返回共享根目录",()->navigateBrowse("",0)); return; }
        String path=listing.optString("path"); browseLocation.setText(listing.optString("rootName","共享文件夹")+(path.isEmpty()?"":" / "+path));
        if(!path.isEmpty()) button(browseList,"‹ 上一级",()->navigateBrowse(path.contains("/")?path.substring(0,path.lastIndexOf('/')):"",0));
        JSONArray entries=listing.optJSONArray("entries");
        if(entries==null || entries.length()==0) browseList.addView(text("此文件夹为空。",14));
        if(entries!=null) for(int i=0;i<entries.length();i++) {
            JSONObject entry=entries.optJSONObject(i); if(entry==null)continue;
            boolean directory=entry.optBoolean("directory");
            String label=(directory?"▸ ":"↓ ")+entry.optString("name");
            if(!directory)label+=" · "+android.text.format.Formatter.formatFileSize(this,entry.optLong("size"));
            if(directory) {
                Button row=button(browseList,label,()->navigateBrowse(entry.optString("path"),0));
                row.setGravity(android.view.Gravity.START|android.view.Gravity.CENTER_VERTICAL);row.setMaxLines(2);
            } else {
                LinearLayout fileRow=new LinearLayout(this);fileRow.setGravity(android.view.Gravity.CENTER_VERTICAL);
                LinearLayout.LayoutParams rowSpace=new LinearLayout.LayoutParams(-1,-2);rowSpace.topMargin=dp(8);browseList.addView(fileRow,rowSpace);
                CheckBox check=new CheckBox(this);check.setTextColor(textColor);check.setMinWidth(dp(48));check.setMinHeight(dp(48));
                check.setContentDescription("选择 "+entry.optString("name"));check.setEnabled(HubFileTasks.isSizeAllowed(entry.optLong("size")));
                check.setChecked(selectedFiles.containsKey(entry.optString("path")));
                check.setOnCheckedChangeListener((v,checked)->{if(checked)selectedFiles.put(entry.optString("path"),entry);else selectedFiles.remove(entry.optString("path"));updateSelection();});
                fileRow.addView(check,new LinearLayout.LayoutParams(dp(48),dp(48)));
                Button row=button(fileRow,entry.optString("name")+"\n"+android.text.format.Formatter.formatFileSize(this,entry.optLong("size")),()->chooseSingleDownload(entry));
                row.setGravity(android.view.Gravity.START|android.view.Gravity.CENTER_VERTICAL);row.setTextSize(16);row.setMaxLines(3);
                LinearLayout.LayoutParams fileSize=new LinearLayout.LayoutParams(0,-2,1);fileSize.leftMargin=dp(8);row.setLayoutParams(fileSize);
                row.setEnabled(HubFileTasks.isSizeAllowed(entry.optLong("size")));
                if(deleteAllowed) {
                    Button more=button(fileRow,"⋮",()->HubStreamUi.hubDialog(this).setTitle(entry.optString("name"))
                            .setItems(new String[]{"移到电脑回收站"},(d,w)->confirmRecycle(entry)).setNegativeButton("取消",null).show());
                    more.setMinWidth(0);more.setContentDescription("文件操作："+entry.optString("name"));
                    LinearLayout.LayoutParams moreSize=new LinearLayout.LayoutParams(dp(48),dp(52));moreSize.leftMargin=dp(8);more.setLayoutParams(moreSize);
                }
            }
        }
        if(browseOffset>0)button(browseList,"上一页",()->navigateBrowse(path,Math.max(0,browseOffset-100)));
        if(!listing.isNull("nextOffset"))button(browseList,"下一页",()->navigateBrowse(path,listing.optInt("nextOffset")));
    }
    private void showFilePane(boolean transfers) {
        showingTasks=transfers;computerPane.setVisibility(transfers?View.GONE:View.VISIBLE);transferPane.setVisibility(transfers?View.VISIBLE:View.GONE);
        computerTab.setSelected(!transfers);tasksTab.setSelected(transfers);
        computerTab.setTextColor(!transfers?textColor:muted);tasksTab.setTextColor(transfers?textColor:muted);
    }
    private void renderRoots(JSONArray roots) {
        knownRoots=roots==null?new JSONArray():roots;
        String signature=browseRootId+knownRoots.toString();if(signature.equals(renderedRoots))return;renderedRoots=signature;rootsList.removeAllViews();
        for(int i=0;i<knownRoots.length();i++){
            JSONObject root=knownRoots.optJSONObject(i);if(root==null)continue;
            button(rootsList,root.optString("name")+(root.optString("id").equals(browseRootId)?" · 当前":""),()->{
                if(checking)return;selectedFiles.clear();updateSelection();browseRootId=root.optString("id");navigateBrowse("",0);
            });
        }
    }
    private void updateSelection() {
        if(downloadSelected==null)return;
        downloadSelected.setText("下载所选文件（"+selectedFiles.size()+"）");
        downloadSelected.setEnabled(fileTransferAllowed && !checking && !selectedFiles.isEmpty());
    }
    private void chooseSingleDownload(JSONObject entry) {selectedFiles.clear();selectedFiles.put(entry.optString("path"),entry);chooseBrowseDestination();}
    private void chooseBrowseDestination() {
        if(checking || !fileTransferAllowed || selectedFiles.isEmpty())return;
        pendingBrowseDownloads=new JSONArray();for(JSONObject entry:selectedFiles.values())pendingBrowseDownloads.put(entry);
        chosenDownload=null;pendingRootId=browseRootId;pendingComputer=store.get("deviceId","");
        startActivityForResult(new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION),OPEN_FOLDER);
    }
    private void queueBrowseDownloads(Uri destination) {
        JSONArray selected=pendingBrowseDownloads;pendingBrowseDownloads=new JSONArray();
        String first=null;
        try {
            if(!pendingComputer.equals(store.get("deviceId","")))throw new java.io.IOException("电脑绑定已改变，请重新选择文件。");
            for(int i=0;i<selected.length();i++) {
                JSONObject entry=selected.getJSONObject(i);
                JSONObject task=tasks.create(store.get("deviceId",""),"download",destination.toString(),
                        entry.getString("name"),entry.getLong("size"),"","");
                task.put("browseRootId",pendingRootId).put("browsePath",entry.getString("path")).put("autoQueued",true);
                tasks.save(task);if(first==null)first=task.getString("id");
            }
            if(first!=null&&!HubFileService.busy())begin(first,false);
            selectedFiles.clear();updateSelection();renderLocal();
            showFilePane(true);
            state.setText("已加入下载队列，同名文件自动另存。");
        } catch(Exception error){state.setText("未添加："+error.getMessage());}
    }
    private void queueUploads(ClipData selection) {
        String first=null;
        try {
            if(!pendingComputer.equals(store.get("deviceId","")))throw new java.io.IOException("电脑绑定已改变，请重新选择文件。");
            for(int i=0;i<selection.getItemCount();i++){
                Uri uri=selection.getItemAt(i).getUri();if(uri==null)continue;
                getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);
                String name="";long size=-1;
                try(Cursor cursor=getContentResolver().query(uri,new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE},null,null,null)){
                    if(cursor!=null&&cursor.moveToFirst()){name=cursor.getString(0);if(!cursor.isNull(1))size=cursor.getLong(1);}
                }
                JSONObject task=tasks.create(store.get("deviceId",""),"upload",uri.toString(),name,size,"","");
                task.put("autoQueued",true);tasks.save(task);if(first==null)first=task.getString("id");
            }
            if(first!=null&&!HubFileService.busy())begin(first,false);renderLocal();
        }catch(Exception error){state.setText("未添加："+error.getMessage());}
    }
    private void confirmRecycle(JSONObject entry) {
        HubStreamUi.hubDialog(this).setTitle("移到电脑回收站？").setMessage(entry.optString("name")+"\n这会移除电脑上的源文件；清理传输记录不会执行此操作。")
                .setNegativeButton("取消",null).setPositiveButton("移到回收站",(d,w)->{
                    final String root=browseRootId;worker.execute(()->{
                        HubFileClient client=new HubFileClient(this);
                        try{client.connect();client.json("recycle",new JSONObject().put("rootId",root).put("path",entry.optString("path")));
                            main.post(()->{if(!isDestroyed())navigateBrowse(browsePath,0);});}
                        catch(Exception error){main.post(()->{if(!isDestroyed())state.setText("未删除："+error.getMessage());});}
                        finally{client.cancel();}
                    });
                }).show();
    }
    private void renderRemote(JSONArray values) {
        remoteList.removeAllViews(); int count=0;
        java.util.Set<String> claimed = new java.util.HashSet<>();
        for(JSONObject task:tasks.list()) if(store.get("deviceId","").equals(task.optString("computer")) && "download".equals(task.optString("direction")) && !"cancelled".equals(task.optString("state"))) claimed.add(task.optString("remoteId"));
        JSONArray retained = new JSONArray();
        if(values!=null) for(int i=0;i<values.length();i++) { JSONObject task=values.optJSONObject(i); if(task==null || !"download".equals(task.optString("direction")) || !"ready".equals(task.optString("state")))continue;
            if(claimed.contains(task.optString("id"))) continue;
            retained.put(task);
            count++; remoteList.addView(text(task.optString("name")+" · "+android.text.format.Formatter.formatFileSize(this,task.optLong("size")),15));
            button(remoteList,"选择文件夹接收",()->{
                chosenDownload=task;pendingBrowseDownloads=new JSONArray();pendingComputer=store.get("deviceId",""); Intent pick=new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION | Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION); startActivityForResult(pick,OPEN_FOLDER);
            });
        }
        remoteTasks=retained;
        if(count==0)remoteList.addView(text("暂无待接收文件，可在电脑“文件”页选择文件发送。",14));
    }
    private void renderLocal() {
        java.util.List<JSONObject> listed=tasks.list(); String signature=listed.toString()+HubFileService.busy();
        renderControls();
        if(signature.equals(renderedTasks))return; renderedTasks=signature;
        renderRemote(remoteTasks);
        localList.removeAllViews();
        if (listed.isEmpty()) localList.addView(text("暂无传输任务。",14));
        for(JSONObject task:listed) {
            String message=task.optString("message");if("running".equals(task.optString("state"))&&!HubFileService.busy())message="传输已中断，可继续。";
            String name=task.optString("name");
            if(message.equals("传输完成 · "+name))message="传输完成";
            String direction="upload".equals(task.optString("direction"))?"平板 → 电脑":"电脑 → 平板";
            String metrics=direction+" · "+android.text.format.Formatter.formatFileSize(this,task.optLong("offset"))+" / "+android.text.format.Formatter.formatFileSize(this,task.optLong("size"));
            if(task.has("elapsedMs"))metrics+=" · 用时 "+task.optLong("elapsedMs")/1000+" 秒";
            if("running".equals(task.optString("state"))&&HubFileService.busy()&&task.has("bytesPerSecond"))metrics+=" · "+android.text.format.Formatter.formatFileSize(this,task.optLong("bytesPerSecond"))+"/s";
            localList.addView(text(name+"\n"+metrics+"\n"+message,15));
            ProgressBar progress = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
            progress.setMax(100); progress.setProgress((int)Math.min(100,Math.max(0,task.optLong("offset")*100/Math.max(1,task.optLong("size")))));
            progress.setProgressTintList(android.content.res.ColorStateList.valueOf(accent));
            localList.addView(progress,new LinearLayout.LayoutParams(-1,dp(8)));
            String stateValue=task.optString("state"),id=task.optString("id");
            if(!"completed".equals(stateValue)&&!"cancelled".equals(stateValue)) {
                button(localList,"继续传输",()->begin(id,false)).setEnabled(!HubFileService.busy()); button(localList,"取消传输（保留源文件）",()->begin(id,true)).setEnabled(!HubFileService.busy());
            }
            if(task.optBoolean("remoteCancelPending"))button(localList,"重试取消电脑任务",()->begin(id,true));
        }
    }
    private void renderControls() {
        if (refreshButton == null) return;
        boolean busy = HubFileService.busy();
        refreshButton.setText(fileTransferAllowed ? "刷新电脑文件" : "申请授权");
        refreshButton.setEnabled(!checking);
        refreshButton.setAlpha(checking ? 0.45f : 1f);
        sendButton.setEnabled(fileTransferAllowed && uploadAllowed);
        sendButton.setAlpha(fileTransferAllowed && uploadAllowed ? 1f : 0.45f);
        sortButton.setEnabled(fileTransferAllowed&&!checking);
        updateSelection();
        pauseButton.setVisibility(busy ? View.VISIBLE : View.GONE);
        java.util.List<JSONObject> listed = tasks.list();
        boolean hasClearable = false;
        for (JSONObject task : listed) {
            String value = task.optString("state");
            if ("completed".equals(value) || "cancelled".equals(value)) { hasClearable = true; break; }
        }
        clearButton.setVisibility(hasClearable ? View.VISIBLE : View.GONE);
    }
    private void begin(String id,boolean discard) {
        if(HubFileService.busy()){state.setText("已有文件正在传输，请先暂停。");return;}
        try {
            if(Build.VERSION.SDK_INT>=33 && checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS)!=android.content.pm.PackageManager.PERMISSION_GRANTED)
                requestPermissions(new String[]{android.Manifest.permission.POST_NOTIFICATIONS},863);
            HubFileService.start(this,id,discard); state.setText(discard?"正在取消任务…":"传输已开始，可在通知中暂停。"); renderControls();
        }catch(Exception error){state.setText("无法开始："+error.getMessage());}
    }
    @Override protected void onActivityResult(int request,int result,Intent data) {
        super.onActivityResult(request,result,data); if(result!=RESULT_OK || data==null){if(request==OPEN_FOLDER)pendingBrowseDownloads=new JSONArray();return;}
        if(request==OPEN_FILE && data.getClipData()!=null){queueUploads(data.getClipData());return;}
        if(data.getData()==null)return;
        Uri uri=data.getData();
        if(request==OPEN_FOLDER && pendingBrowseDownloads.length()>0) {
            try{getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);queueBrowseDownloads(uri);}catch(Exception error){state.setText("未添加："+error.getMessage());}
            return;
        }
        try {
            if(!pendingComputer.equals(store.get("deviceId","")))throw new java.io.IOException("电脑绑定已改变，请重新选择文件。");
            if(request==OPEN_FILE) getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION);
            else getContentResolver().takePersistableUriPermission(uri,Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            JSONObject task;
            if(request==OPEN_FILE) {
                String name=""; long size=-1;
                try(Cursor cursor=getContentResolver().query(uri,new String[]{OpenableColumns.DISPLAY_NAME,OpenableColumns.SIZE},null,null,null)) {
                    if(cursor!=null&&cursor.moveToFirst()){ name=cursor.getString(0); if(!cursor.isNull(1))size=cursor.getLong(1); }
                }
                task=tasks.create(store.get("deviceId",""),"upload",uri.toString(),name,size,"","");
            }else if(request==OPEN_FOLDER && chosenDownload!=null) {
                task=tasks.create(store.get("deviceId",""),"download",uri.toString(),chosenDownload.getString("name"),chosenDownload.getLong("size"),chosenDownload.getString("id"),chosenDownload.getString("sha256")); chosenDownload=null;
            }else return;
            task.put("autoQueued",true);tasks.save(task);if(!HubFileService.busy())begin(task.getString("id"),false);showFilePane(true);renderLocal();
        }catch(Exception error){state.setText("未添加："+error.getMessage());}
    }
    @Override protected void onResume(){super.onResume();foreground=true;main.post(refresh);}
    @Override protected void onSaveInstanceState(Bundle out){super.onSaveInstanceState(out);if(chosenDownload!=null)out.putString("download",chosenDownload.toString());out.putString("browseRootId",browseRootId);out.putString("browseSort",browseSort);JSONArray selected=new JSONArray();for(JSONObject entry:selectedFiles.values())selected.put(entry);out.putString("selectedFiles",selected.toString());out.putString("pendingRootId",pendingRootId);out.putString("pendingComputer",pendingComputer);out.putBoolean("showingTasks",showingTasks);out.putString("pendingBrowseDownloads",pendingBrowseDownloads.toString());out.putString("browsePath",browsePath);out.putInt("browseOffset",browseOffset);}
    @Override protected void onPause(){foreground=false;main.removeCallbacks(refresh);super.onPause();}
    @Override protected void onDestroy(){if(control!=null)control.cancel();worker.shutdownNow();main.removeCallbacks(refresh);super.onDestroy();}
}
