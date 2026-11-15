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
import android.view.Gravity;
import com.limelight.R;
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
    private static final int OPEN_FILE=861, OPEN_FOLDER=862, CHANGE_FOLDER=864;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private final Handler main = new Handler(Looper.getMainLooper());
    private HubStore store; private HubFileTasks tasks;
    private Button pauseButton;
    private ImageButton destinationButton, sortButton, refreshButton, moreButton;
    private boolean compactToolbar;
    private View sendButton;
    private Button clearButton, rootChoice;
    private TextView state, browseLocation; private LinearLayout localList, remoteList, browseList;
    private String browsePath = "", renderedBrowse = "", browseSort="name";
    private int browseOffset;
    private String browseRootId="", pendingRootId="", pendingComputer="", renderedRoots="";
    private Button computerTab,tasksTab;
    private boolean showingTasks;
    private LinearLayout rootsList,computerPane,transferPane;
    private Button downloadSelected, selectAllButton, requestAuthorization;
    private LinearLayout selectionBar, browserContent, authorizationPane;
    private TextView authorizationMessage;
    private ScrollView contentScroll;
    private LinearLayout body, transferEmpty;
    private TextView remoteHeading, localHeading;
    private String browseError="";
    private final java.util.LinkedHashMap<String,JSONObject> selectableFiles=new java.util.LinkedHashMap<>();
    private final java.util.List<CheckBox> fileChecks=new java.util.ArrayList<>();
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
        FrameLayout centered=new FrameLayout(this); centered.setBackgroundColor(background);
        HubWindowLayout layout=HubWindowLayout.of(this);
        LinearLayout content=new LinearLayout(this); content.setOrientation(LinearLayout.VERTICAL); content.setPadding(dp(layout.pageInsetDp()),dp(layout.compact()?12:20),dp(layout.pageInsetDp()),dp(12));
        int fileWidth=Math.max(1,Math.min(1000,layout.widthDp-layout.railWidthDp()));
        centered.addView(content,new FrameLayout.LayoutParams(dp(fileWidth),-1,Gravity.TOP|Gravity.CENTER_HORIZONTAL));
        HubNavigation navigation=new HubNavigation(this,centered,3,index->{
            if(index==3)return;
            startActivity(new Intent(this,HubActivity.class).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP|Intent.FLAG_ACTIVITY_SINGLE_TOP).putExtra("HubPage",index), HubNavigation.transition(this));
            HubNavigation.holdSurface(this);
        }); setContentView(navigation.view);
        LinearLayout toolbar=new LinearLayout(this); toolbar.setGravity(Gravity.CENTER_VERTICAL);
        TextView title=text("文件",layout.compact()?20:24); title.setTypeface(null,Typeface.BOLD); title.setSingleLine(true);title.setEllipsize(android.text.TextUtils.TruncateAt.END);
        LinearLayout.LayoutParams titleSize=new LinearLayout.LayoutParams(0,-2,1); titleSize.leftMargin=dp(4); toolbar.addView(title,titleSize);
        refreshButton=iconAction(toolbar,R.drawable.ic_hub_refresh,"刷新电脑文件",this::loadRemote);
        sortButton=iconAction(toolbar,R.drawable.ic_hub_sort,"排序："+sortLabel(),this::showSortChoices);
        destinationButton=iconAction(toolbar,R.drawable.ic_hub_folder,"保存位置",()->pickDestination(CHANGE_FOLDER)); updateDestinationLabel();
        compactToolbar=layout.widthDp-layout.railWidthDp()<480;
        if(compactToolbar) {
            ((View)sortButton.getParent()).setVisibility(View.GONE);
            ((View)destinationButton.getParent()).setVisibility(View.GONE);
            moreButton=iconAction(toolbar,R.drawable.ic_hub_more,"更多文件操作",()->HubStreamUi.hubDialog(this)
                    .setItems(new String[]{"排序", "保存位置"},(dialog,which)->{
                        if(which==0) {
                            if(sortButton.isEnabled())showSortChoices();
                            else Toast.makeText(this,sortButton.getContentDescription(),Toast.LENGTH_SHORT).show();
                        } else pickDestination(CHANGE_FOLDER);
                    }).show());
        }
        {
            FrameLayout sendHint=new HintFrame();toolbar.addView(sendHint,new LinearLayout.LayoutParams(-2,dp(48)));
            LinearLayout sendSlot=new LinearLayout(this);sendHint.addView(sendSlot);
            Button send=button(sendSlot,"发送文件",true,this::chooseUpload);
            send.setTextSize(14);send.setSingleLine(true);send.setMinWidth(0);send.setMinHeight(dp(48));send.setPadding(dp(10),0,dp(10),0);
            send.setLayoutParams(new LinearLayout.LayoutParams(-2,dp(48)));send.setTag(sendHint);sendButton=send;
        }
        toolbar.setMinimumHeight(dp(56));content.addView(toolbar,new LinearLayout.LayoutParams(-1,-2));
        LinearLayout statusBar=new LinearLayout(this);statusBar.setGravity(Gravity.CENTER_VERTICAL);
        state=text("正在连接电脑…",13); state.setTextColor(muted);state.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        statusBar.addView(state,new LinearLayout.LayoutParams(0,-2,1));
        content.addView(statusBar,new LinearLayout.LayoutParams(-1,-2));
        LinearLayout tabs=new LinearLayout(this);
        computerTab=button(tabs,"电脑文件",()->showFilePane(false));
        tasksTab=button(tabs,"传输任务",()->showFilePane(true));
        for(int i=0;i<tabs.getChildCount();i++){Button tab=(Button)tabs.getChildAt(i);tab.setTextSize(14);tab.setMinHeight(dp(48));tab.setPadding(dp(8),dp(4),dp(8),dp(4));LinearLayout.LayoutParams tabSize=new LinearLayout.LayoutParams(0,-2,1);if(i>0)tabSize.leftMargin=dp(8);tab.setLayoutParams(tabSize);}
        content.addView(tabs,new LinearLayout.LayoutParams(-1,-2));
        contentScroll=new ScrollView(this);contentScroll.setFillViewport(true);contentScroll.setClipToPadding(false);content.addView(contentScroll,new LinearLayout.LayoutParams(-1,0,1));
        body=new LinearLayout(this);body.setOrientation(LinearLayout.VERTICAL);contentScroll.addView(body);
        computerPane=new LinearLayout(this);computerPane.setOrientation(LinearLayout.VERTICAL);body.addView(computerPane,new LinearLayout.LayoutParams(-1,-2));
        authorizationPane=emptyPane("连接电脑后可浏览文件。");
        authorizationPane.setMinimumHeight(dp(260));computerPane.addView(authorizationPane,new LinearLayout.LayoutParams(-1,-2));
        authorizationMessage=(TextView)authorizationPane.getChildAt(1);
        requestAuthorization=button(authorizationPane,"重试连接",this::requestFileAuthorization);compactButton(requestAuthorization);
        ((LinearLayout.LayoutParams)requestAuthorization.getLayoutParams()).gravity=Gravity.CENTER_HORIZONTAL;
        browserContent=new LinearLayout(this);browserContent.setOrientation(LinearLayout.VERTICAL);computerPane.addView(browserContent,new LinearLayout.LayoutParams(-1,-2));
        rootsList=new LinearLayout(this);rootsList.setOrientation(LinearLayout.VERTICAL);browserContent.addView(rootsList);
        browseLocation=text("",14);browserContent.addView(browseLocation);
        browseList=new LinearLayout(this);browseList.setOrientation(LinearLayout.VERTICAL);browserContent.addView(browseList);
        transferPane=new LinearLayout(this);transferPane.setOrientation(LinearLayout.VERTICAL);transferPane.setVisibility(View.GONE);body.addView(transferPane);
        transferEmpty=emptyPane("暂无传输任务");
        body.addView(transferEmpty,new LinearLayout.LayoutParams(-1,-2));
        selectionBar=new LinearLayout(this);selectionBar.setGravity(Gravity.CENTER_VERTICAL);selectionBar.setPadding(0,dp(8),0,0);selectionBar.setVisibility(View.GONE);
        selectAllButton=button(selectionBar,"全选",()->selectVisibleFiles(true));compactButton(selectAllButton);
        Button cancelSelection=button(selectionBar,"取消",()->selectVisibleFiles(false));compactButton(cancelSelection);
        Space selectionGap=new Space(this);selectionBar.addView(selectionGap,new LinearLayout.LayoutParams(0,1,1));
        HintFrame downloadHint=new HintFrame();selectionBar.addView(downloadHint,new LinearLayout.LayoutParams(-2,-2));
        LinearLayout downloadSlot=new LinearLayout(this);downloadHint.addView(downloadSlot);
        downloadSelected=button(downloadSlot,"下载（0）",true,this::chooseBrowseDestination);compactButton(downloadSelected);downloadSelected.setTag(downloadHint);
        downloadSelected.setContentDescription("下载所选文件");content.addView(selectionBar,new LinearLayout.LayoutParams(-1,-2));
        selectionBar.addOnLayoutChangeListener((v,l,t,r,b,ol,ot,or,ob)->reserveSelectionSpace());
        contentScroll.addOnLayoutChangeListener((v,l,t,r,b,ol,ot,or,ob)->{
            int height=Math.max(0,b-t-contentScroll.getPaddingTop()-contentScroll.getPaddingBottom());
            authorizationPane.setMinimumHeight(height);transferEmpty.setMinimumHeight(height);
        });
        pauseButton=button(transferPane,"暂停当前传输",()->{ HubFileService.pause(); state.setText("正在暂停，已确认进度会保留。"); });
        remoteHeading=heading("电脑发来的文件");transferPane.addView(remoteHeading); remoteList=new LinearLayout(this); remoteList.setOrientation(LinearLayout.VERTICAL); transferPane.addView(remoteList);
        localHeading=heading("本机传输任务");transferPane.addView(localHeading); localList=new LinearLayout(this); localList.setOrientation(LinearLayout.VERTICAL); transferPane.addView(localList);
        clearButton=button(transferPane,"清理已完成/取消记录",()->{ tasks.clearFinished(); renderLocal(); });

        compactButton(pauseButton);compactButton(clearButton);showFilePane(showingTasks);renderControls();
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
        Button button=new Button(this); button.setText(label); HubButtons.apply(button,primary); button.setMinHeight(dp(52));
        LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(-1,-2); params.topMargin=dp(8); parent.addView(button,params); button.setOnClickListener(v->action.run()); return button;
    }
    private void compactButton(Button button) {
        button.setMinWidth(0);button.setTextSize(14);button.setMinHeight(dp(48));
        button.setLayoutParams(new LinearLayout.LayoutParams(-2,dp(48)));
    }
    private LinearLayout emptyPane(String message) {
        LinearLayout pane=new LinearLayout(this);pane.setOrientation(LinearLayout.VERTICAL);pane.setGravity(Gravity.CENTER);pane.setPadding(dp(20),dp(28),dp(20),dp(28));
        ImageView icon=new ImageView(this);icon.setImageResource(R.drawable.ic_nav_files);icon.setImageTintList(android.content.res.ColorStateList.valueOf(muted));icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);
        pane.addView(icon,new LinearLayout.LayoutParams(dp(40),dp(40)));
        TextView label=text(message,15);label.setTextColor(muted);label.setGravity(Gravity.CENTER);pane.addView(label,new LinearLayout.LayoutParams(-1,-2));return pane;
    }
    private ImageButton iconAction(LinearLayout parent,int icon,String description,Runnable action) {
        HintFrame hint=new HintFrame();parent.addView(hint,new LinearLayout.LayoutParams(dp(48),dp(48)));
        ImageButton button=new ImageButton(this);button.setImageResource(icon);button.setImageTintList(android.content.res.ColorStateList.valueOf(accent));
        button.setPadding(dp(12),dp(12),dp(12),dp(12));button.setBackgroundColor(android.graphics.Color.TRANSPARENT);
        button.setContentDescription(description);button.setOnClickListener(v->action.run());button.setTag(hint);hint.addView(button,new FrameLayout.LayoutParams(-1,-1));
        if(Build.VERSION.SDK_INT>=26)button.setTooltipText(description);return button;
    }
    /** Disabled actions still explain their reason, without making the action executable. */
    private final class HintFrame extends FrameLayout {
        HintFrame(){super(HubFilesActivity.this);setClickable(true);setOnLongClickListener(v->{showHint();return true;});setOnClickListener(v->showHint());}
        private boolean actionEnabled(){View child=getChildAt(0);if(child instanceof LinearLayout)child=((LinearLayout)child).getChildAt(0);return child!=null&&child.isEnabled();}
        @Override public boolean onInterceptTouchEvent(android.view.MotionEvent event){return !actionEnabled()||super.onInterceptTouchEvent(event);}
        private void showHint(){Toast.makeText(HubFilesActivity.this,getContentDescription(),Toast.LENGTH_SHORT).show();}
    }
    private void actionAvailability(View view,boolean enabled,String description) {
        view.setEnabled(enabled);view.setAlpha(enabled?1f:0.45f);view.setContentDescription(description);
        if(Build.VERSION.SDK_INT>=26)view.setTooltipText(description);
        if(view.getTag() instanceof HintFrame)((HintFrame)view.getTag()).setContentDescription(description);
    }
    private void chooseUpload() {
        if(!fileTransferAllowed||!uploadAllowed||checking)return;
        pendingComputer=store.get("deviceId","");
        Intent pick=new Intent(Intent.ACTION_OPEN_DOCUMENT).putExtra(Intent.EXTRA_ALLOW_MULTIPLE,true).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);startActivityForResult(pick,OPEN_FILE);
    }
    private void requestFileAuthorization() {
        if(store.bound())loadRemote();
        else startActivity(new Intent(this,HubActivity.class).addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP|Intent.FLAG_ACTIVITY_SINGLE_TOP).putExtra("HubPage",1),HubNavigation.transition(this));
    }
    private void loadRemote() {
        if(!store.bound()) { fileTransferAllowed=false;state.setText("先在“全部设备”添加电脑。");renderControls();return; }
        if(checking) return; checking=true; state.setText("正在连接电脑…");
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
                String browseError="电脑尚未添加可浏览目录。";
                if(allowed && enabled) {
                    if(client.supportsRoots()&&(roots==null||roots.length()==0))browseError="请在电脑的设备权限中添加可浏览目录。";
                    else if(client.supportsBrowse()) try { listing=client.json("browse",new JSONObject().put("path",requestedPath).put("offset",requestedOffset).put("rootId",currentRoot).put("sort",requestedSort)).optJSONObject("listing"); }
                    catch(Exception error) { browseError=error.getMessage(); }
                    else browseError="电脑端尚未支持共享目录浏览，请更新电脑端。";
                }
                final JSONArray resolvedRoots=roots;
                final JSONObject shared=listing; final String sharedMessage=browseError;
                String message=allowed?(enabled?"已连接":"电脑尚未开启文件访问"):registration.optBoolean("usesDevicePermissions")?"请在电脑的设备权限中开启“文件”。":"旧版电脑需要核对文件权限，建议更新电脑端。";
                main.post(()->{ if(isDestroyed()||!requestedComputer.equals(store.get("deviceId",""))) return; browseRootId=currentRoot;fileTransferAllowed=allowed && enabled; uploadAllowed=registration.optBoolean("uploadAllowed",true);deleteAllowed=registration.optBoolean("deleteAllowed",false);renderRoots(resolvedRoots); state.setText(message); remoteTasks=available; renderRemote(remoteTasks); renderBrowse(shared,sharedMessage); renderControls(); });
            }catch(Exception error) { main.post(()->{ if(!isDestroyed()) { fileTransferAllowed=false;deleteAllowed=false;renderRoots(new JSONArray());state.setText("未连接："+error.getMessage()); renderRemote(new JSONArray()); renderBrowse(null,"连接电脑后可浏览共享文件夹。"); renderControls(); } }); }
            finally { if(control!=null)control.cancel(); control=null; main.post(()->{checking=false;nextRemoteRefreshAt=SystemClock.elapsedRealtime()+10000;if(!isDestroyed())renderControls();}); }
        });
    }
    private void navigateBrowse(String path,int offset) {
        if(checking)return;
        selectedFiles.clear();renderSelectionBar();browsePath=path; browseOffset=offset; renderedBrowse=""; nextRemoteRefreshAt=0; loadRemote();
    }
    private String sortLabel(){return "modified".equals(browseSort)?"最近修改":"size".equals(browseSort)?"文件从大到小":"name-desc".equals(browseSort)?"名称 Z–A":"名称 A–Z";}
    private void showSortChoices(){
        if(checking)return;
        String[] values={"name","name-desc","modified","size"};int selected=java.util.Arrays.asList(values).indexOf(browseSort);
        HubStreamUi.hubDialog(this).setTitle("电脑文件排序").setSingleChoiceItems(new String[]{"名称 A–Z","名称 Z–A","最近修改","文件从大到小"},Math.max(0,selected),(d,w)->{
            d.dismiss();browseSort=values[w];sortButton.setContentDescription("排序："+sortLabel());navigateBrowse(browsePath,0);
        }).setNegativeButton("取消",null).show();
    }
    private void renderBrowse(JSONObject listing,String error) {
        browseError=listing==null?error:"";
        String signature=browseRootId+":"+deleteAllowed+":"+(listing==null?error:listing.toString());
        if(signature.equals(renderedBrowse)){renderSelectionBar();return;} renderedBrowse=signature;browseList.removeAllViews();selectableFiles.clear();fileChecks.clear();
        if(listing==null) { selectedFiles.clear();renderSelectionBar();browseLocation.setText("");browseList.addView(emptyPane(error)); if(!browsePath.isEmpty() || browseOffset>0) button(browseList,"返回共享根目录",()->navigateBrowse("",0)); return; }
        String path=listing.optString("path"); browseLocation.setText(listing.optString("rootName","共享文件夹")+(path.isEmpty()?"":" / "+path));
        if(!path.isEmpty()) button(browseList,"‹ 上一级",()->navigateBrowse(path.contains("/")?path.substring(0,path.lastIndexOf('/')):"",0));
        JSONArray entries=listing.optJSONArray("entries");
        if(entries==null || entries.length()==0) browseList.addView(emptyPane("此文件夹为空"));
        if(entries!=null) for(int i=0;i<entries.length();i++) {
            JSONObject entry=entries.optJSONObject(i); if(entry==null)continue;
            boolean directory=entry.optBoolean("directory");
            if(directory) {
                LinearLayout folderRow=new LinearLayout(this);folderRow.setGravity(Gravity.CENTER_VERTICAL);folderRow.setPadding(dp(12),dp(4),dp(8),dp(4));folderRow.setMinimumHeight(dp(56));
                GradientDrawable folderBackground=new GradientDrawable();folderBackground.setColor(field);folderBackground.setCornerRadius(dp(6));
                folderRow.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(ripple),folderBackground,null));
                ImageView icon=new ImageView(this);icon.setImageResource(R.drawable.ic_hub_folder);icon.setImageTintList(android.content.res.ColorStateList.valueOf(accent));icon.setImportantForAccessibility(View.IMPORTANT_FOR_ACCESSIBILITY_NO);folderRow.addView(icon,new LinearLayout.LayoutParams(dp(24),dp(24)));
                TextView folderName=text(entry.optString("name"),15);folderName.setMaxLines(2);folderName.setEllipsize(android.text.TextUtils.TruncateAt.END);LinearLayout.LayoutParams nameSize=new LinearLayout.LayoutParams(0,-2,1);nameSize.leftMargin=dp(12);folderRow.addView(folderName,nameSize);
                TextView arrow=text("›",20);arrow.setTextColor(muted);folderRow.addView(arrow,new LinearLayout.LayoutParams(dp(24),-2));
                folderRow.setContentDescription("文件夹："+entry.optString("name"));folderRow.setFocusable(true);folderRow.setOnClickListener(v->navigateBrowse(entry.optString("path"),0));
                folderRow.setAccessibilityDelegate(new View.AccessibilityDelegate(){@Override public void onInitializeAccessibilityNodeInfo(View host,android.view.accessibility.AccessibilityNodeInfo info){super.onInitializeAccessibilityNodeInfo(host,info);info.setClassName(Button.class.getName());}});
                LinearLayout.LayoutParams folderSize=new LinearLayout.LayoutParams(-1,-2);folderSize.topMargin=dp(4);browseList.addView(folderRow,folderSize);
            } else {
                LinearLayout fileRow=new LinearLayout(this);fileRow.setGravity(android.view.Gravity.CENTER_VERTICAL);
                fileRow.setMinimumHeight(dp(56));LinearLayout.LayoutParams rowSpace=new LinearLayout.LayoutParams(-1,-2);rowSpace.topMargin=dp(4);browseList.addView(fileRow,rowSpace);
                if(HubFileTasks.isSizeAllowed(entry.optLong("size")))selectableFiles.put(entry.optString("path"),entry);
                CheckBox check=new CheckBox(this);check.setTag(entry.optString("path"));fileChecks.add(check);check.setTextColor(textColor);check.setMinWidth(dp(48));check.setMinHeight(dp(48));
                check.setButtonTintList(new android.content.res.ColorStateList(new int[][]{new int[]{android.R.attr.state_checked},new int[]{}},new int[]{accent,muted}));
                check.setContentDescription("选择 "+entry.optString("name"));check.setEnabled(HubFileTasks.isSizeAllowed(entry.optLong("size")));
                check.setChecked(selectedFiles.containsKey(entry.optString("path")));
                check.setOnCheckedChangeListener((v,checked)->{if(checked)selectedFiles.put(entry.optString("path"),entry);else selectedFiles.remove(entry.optString("path"));renderSelectionBar();});
                fileRow.addView(check,new LinearLayout.LayoutParams(dp(48),dp(48)));
                Button row=button(fileRow,entry.optString("name")+"\n"+android.text.format.Formatter.formatFileSize(this,entry.optLong("size")),()->chooseSingleDownload(entry));
                row.setGravity(android.view.Gravity.START|android.view.Gravity.CENTER_VERTICAL);row.setTextSize(14);row.setMaxLines(3);row.setMinHeight(dp(56));row.setMinimumHeight(dp(56));row.setPadding(dp(8),dp(4),dp(8),dp(4));row.setBackgroundColor(android.graphics.Color.TRANSPARENT);
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
        selectedFiles.keySet().retainAll(selectableFiles.keySet());renderSelectionBar();
    }
    private void showFilePane(boolean transfers) {
        showingTasks=transfers;computerPane.setVisibility(transfers?View.GONE:View.VISIBLE);
        computerTab.setSelected(!transfers);tasksTab.setSelected(transfers);
        styleTab(computerTab,!transfers);styleTab(tasksTab,transfers);renderControls();
    }
    private void styleTab(Button tab, boolean selected) {
        tab.setTextColor(selected?accent:muted);
        tab.setTypeface(null,selected?Typeface.BOLD:Typeface.NORMAL);
        GradientDrawable base=new GradientDrawable();base.setColor(android.graphics.Color.TRANSPARENT);
        GradientDrawable line=new GradientDrawable();line.setColor(selected?accent:border);
        android.graphics.drawable.LayerDrawable layers=new android.graphics.drawable.LayerDrawable(new android.graphics.drawable.Drawable[]{base,line});
        layers.setLayerGravity(1,android.view.Gravity.BOTTOM);layers.setLayerHeight(1,dp(selected?2:1));
        tab.setBackground(new RippleDrawable(android.content.res.ColorStateList.valueOf(ripple),layers,null));
    }
    private void renderRoots(JSONArray roots) {
        knownRoots=roots==null?new JSONArray():roots;
        String signature=browseRootId+knownRoots.toString();if(signature.equals(renderedRoots))return;renderedRoots=signature;rootsList.removeAllViews();
        if(knownRoots.length()==0)return;
        String name="选择电脑目录";for(int i=0;i<knownRoots.length();i++){JSONObject root=knownRoots.optJSONObject(i);if(root!=null&&root.optString("id").equals(browseRootId))name=root.optString("name");}
        rootChoice=button(rootsList,name+"  ▾",()->{
            if(checking)return;String[] labels=new String[knownRoots.length()];int selected=-1;
            for(int i=0;i<labels.length;i++){JSONObject root=knownRoots.optJSONObject(i);labels[i]=root.optString("name");if(root.optString("id").equals(browseRootId))selected=i;}
            HubStreamUi.hubDialog(this).setTitle("电脑目录").setSingleChoiceItems(labels,selected,(d,w)->{d.dismiss();selectedFiles.clear();renderSelectionBar();browseRootId=knownRoots.optJSONObject(w).optString("id");navigateBrowse("",0);}).setNegativeButton("取消",null).show();
        });
        rootChoice.setTextSize(14);rootChoice.setGravity(Gravity.START|Gravity.CENTER_VERTICAL);rootChoice.setMinHeight(dp(48));rootChoice.setPadding(dp(12),dp(4),dp(12),dp(4));
        rootChoice.setContentDescription("选择电脑目录，当前："+name);
    }
    private void renderSelectionBar() {
        if(selectionBar==null)return;
        selectionBar.setVisibility(!showingTasks&&fileTransferAllowed&&!selectedFiles.isEmpty()?View.VISIBLE:View.GONE);
        downloadSelected.setText("下载（"+selectedFiles.size()+"）");
        actionAvailability(downloadSelected,fileTransferAllowed&&!checking&&!selectedFiles.isEmpty(),checking?"正在读取电脑文件，请稍候":"下载所选文件");
        selectAllButton.setEnabled(!checking&&!selectableFiles.isEmpty());
        reserveSelectionSpace();
    }
    private void reserveSelectionSpace() {
        if(contentScroll==null||selectionBar==null)return;
        int bottom=selectionBar.getVisibility()==View.VISIBLE?Math.max(dp(56),selectionBar.getHeight())+dp(12):dp(12);
        if(contentScroll.getPaddingBottom()!=bottom)contentScroll.setPadding(0,0,0,bottom);
    }
    private void selectVisibleFiles(boolean all) {
        if(checking&&all)return;
        selectedFiles.clear();if(all)selectedFiles.putAll(selectableFiles);
        for(CheckBox check:fileChecks)check.setChecked(selectedFiles.containsKey(check.getTag()));
        renderSelectionBar();
    }
    private void chooseSingleDownload(JSONObject entry) {
        if(checking||!fileTransferAllowed)return;
        selectedFiles.clear();selectedFiles.put(entry.optString("path"),entry);
        for(CheckBox check:fileChecks)check.setChecked(selectedFiles.containsKey(check.getTag()));
        renderSelectionBar();chooseBrowseDestination();
    }
    private void chooseBrowseDestination() {
        if(checking || !fileTransferAllowed || selectedFiles.isEmpty())return;
        pendingBrowseDownloads=new JSONArray();for(JSONObject entry:selectedFiles.values())pendingBrowseDownloads.put(entry);
        chosenDownload=null;pendingRootId=browseRootId;pendingComputer=store.get("deviceId","");
        Uri saved=savedDestination();
        if(saved!=null)queueBrowseDownloads(saved);else pickDestination(OPEN_FOLDER);
    }
    private android.content.SharedPreferences filePreferences() { return getSharedPreferences("hub_file_preferences",MODE_PRIVATE); }
    private Uri savedDestination() {
        String value=filePreferences().getString("downloadTree","");if(value.isEmpty())return null;
        try {
            Uri uri=Uri.parse(value);
            for(android.content.UriPermission grant:getContentResolver().getPersistedUriPermissions()) {
                if(uri.equals(grant.getUri())&&grant.isWritePermission()) {
                    androidx.documentfile.provider.DocumentFile tree=androidx.documentfile.provider.DocumentFile.fromTreeUri(this,uri);
                    if(tree!=null&&tree.exists()&&tree.isDirectory()&&tree.canWrite())return uri;
                }
            }
        }catch(RuntimeException ignored) { }
        return null;
    }
    private void pickDestination(int request) {
        Intent pick=new Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION|Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION);
        Uri saved=savedDestination();if(saved!=null)pick.putExtra(android.provider.DocumentsContract.EXTRA_INITIAL_URI,saved);
        if(request==OPEN_FOLDER)HubStreamUi.hubDialog(this).setTitle("选择保存位置").setMessage("接下来选择本机保存文件的文件夹，之后下载会沿用这个位置。")
            .setPositiveButton("选择文件夹",(d,w)->startActivityForResult(pick,request)).setNegativeButton("取消",(d,w)->pendingBrowseDownloads=new JSONArray()).show();
        else startActivityForResult(pick,request);
    }
    private void rememberDestination(Uri uri, Intent result) {
        int flags=result.getFlags()&(Intent.FLAG_GRANT_READ_URI_PERMISSION|Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        if((flags&Intent.FLAG_GRANT_WRITE_URI_PERMISSION)==0)throw new SecurityException("所选目录没有写入权限");
        getContentResolver().takePersistableUriPermission(uri,flags);
        String old=filePreferences().getString("downloadTree","");
        filePreferences().edit().putString("downloadTree",uri.toString()).apply();
        // Existing queued transfers may still use an earlier directory; retain those grants.
        if(!old.equals(uri.toString()))updateDestinationLabel();
    }
    private void updateDestinationLabel() {
        if(destinationButton==null)return;
        Uri uri=savedDestination();String name="";
        if(uri!=null)try { androidx.documentfile.provider.DocumentFile tree=androidx.documentfile.provider.DocumentFile.fromTreeUri(this,uri);if(tree!=null)name=tree.getName(); }catch(RuntimeException ignored) { }
        String description=uri==null?"保存位置未设置，首次下载需选择文件夹":"保存位置："+(name==null?"已选目录":name);
        actionAvailability(destinationButton,true,description);
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
            selectedFiles.clear();renderSelectionBar();renderLocal();
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
            if(first!=null&&!HubFileService.busy())begin(first,false);showFilePane(true);renderLocal();
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
            button(remoteList,"接收文件",()->{
                chosenDownload=task;pendingBrowseDownloads=new JSONArray();pendingComputer=store.get("deviceId","");
                Uri destination=savedDestination();if(destination==null)pickDestination(OPEN_FOLDER);else queueIncomingDownload(destination);
            });
        }
        remoteTasks=retained;
        renderTaskVisibility();
    }
    private void queueIncomingDownload(Uri destination) {
        try {
            if(!pendingComputer.equals(store.get("deviceId",""))||chosenDownload==null)throw new java.io.IOException("电脑绑定已改变，请重新选择文件");
            JSONObject incoming=chosenDownload;
            JSONObject task=tasks.create(pendingComputer,"download",destination.toString(),incoming.getString("name"),incoming.getLong("size"),incoming.getString("id"),incoming.getString("sha256"));
            task.put("autoQueued",true);tasks.save(task);chosenDownload=null;
            if(!HubFileService.busy())begin(task.getString("id"),false);showFilePane(true);renderLocal();
        }catch(Exception error){state.setText("未添加："+error.getMessage());}
    }
    private void renderLocal() {
        java.util.List<JSONObject> listed=tasks.list(); String signature=listed.toString()+HubFileService.busy();
        renderControls();
        if(signature.equals(renderedTasks))return; renderedTasks=signature;
        renderRemote(remoteTasks);
        localList.removeAllViews();
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
        renderTaskVisibility();
    }
    private void renderTaskVisibility() {
        if(remoteHeading==null)return;
        boolean hasRemote=remoteTasks.length()>0,hasLocal=!tasks.list().isEmpty();
        remoteHeading.setVisibility(hasRemote?View.VISIBLE:View.GONE);remoteList.setVisibility(hasRemote?View.VISIBLE:View.GONE);
        localHeading.setVisibility(hasLocal?View.VISIBLE:View.GONE);localList.setVisibility(hasLocal?View.VISIBLE:View.GONE);
        transferPane.setVisibility(showingTasks&&(hasRemote||hasLocal)?View.VISIBLE:View.GONE);
        transferEmpty.setVisibility(showingTasks&&!hasRemote&&!hasLocal?View.VISIBLE:View.GONE);
    }
    private void renderControls() {
        if (refreshButton == null) return;
        boolean busy = HubFileService.busy();
        actionAvailability(refreshButton,!checking,checking?"正在读取电脑文件，请稍候":"刷新电脑文件");
        boolean canSend=fileTransferAllowed&&uploadAllowed&&!checking;
        actionAvailability(sendButton,canSend,checking?"正在读取电脑文件，请稍候":!fileTransferAllowed?"电脑设备权限中的“文件”未开启":!uploadAllowed?"电脑未允许此设备上传文件":"发送文件");
        actionAvailability(sortButton,fileTransferAllowed&&!checking,checking?"正在读取电脑文件，请稍候":!fileTransferAllowed?"电脑设备权限中的“文件”未开启":"排序："+sortLabel());
        boolean canBrowse=fileTransferAllowed&&(knownRoots.length()>0||browseError.isEmpty());
        browserContent.setVisibility(canBrowse?View.VISIBLE:View.GONE);
        authorizationPane.setVisibility(canBrowse?View.GONE:View.VISIBLE);
        authorizationMessage.setText(!fileTransferAllowed?state.getText():browseError);
        state.setVisibility((showingTasks?fileTransferAllowed||busy:canBrowse)?View.VISIBLE:View.GONE);
        requestAuthorization.setText(store.bound()?"重试连接":"添加电脑");requestAuthorization.setEnabled(!checking);
        if(rootChoice!=null)rootChoice.setEnabled(!checking);
        int auxiliaryVisibility=showingTasks||compactToolbar?View.GONE:View.VISIBLE;
        sortButton.setVisibility(auxiliaryVisibility);((View)sortButton.getParent()).setVisibility(auxiliaryVisibility);
        destinationButton.setVisibility(auxiliaryVisibility);((View)destinationButton.getParent()).setVisibility(auxiliaryVisibility);
        if(moreButton!=null)((View)moreButton.getParent()).setVisibility(showingTasks?View.GONE:View.VISIBLE);
        sendButton.setVisibility(showingTasks?View.GONE:View.VISIBLE);((View)sendButton.getTag()).setVisibility(showingTasks?View.GONE:View.VISIBLE);
        renderSelectionBar();
        pauseButton.setVisibility(busy ? View.VISIBLE : View.GONE);
        java.util.List<JSONObject> listed = tasks.list();
        boolean hasClearable = false;
        for (JSONObject task : listed) {
            String value = task.optString("state");
            if ("completed".equals(value) || "cancelled".equals(value)) { hasClearable = true; break; }
        }
        clearButton.setVisibility(hasClearable ? View.VISIBLE : View.GONE);
        renderTaskVisibility();
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
        if(request==CHANGE_FOLDER){try{rememberDestination(uri,data);}catch(Exception error){state.setText("无法保存位置："+error.getMessage());}return;}
        if(request==OPEN_FOLDER){
            try { rememberDestination(uri,data); }catch(Exception error){state.setText("无法保存位置："+error.getMessage());return;}
            if(pendingBrowseDownloads.length()>0)queueBrowseDownloads(uri);else queueIncomingDownload(uri);
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
