package com.limelight.streamhub;

import android.os.Bundle;
import android.view.View;
import android.view.ViewGroup;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.TextView;
import org.robolectric.RuntimeEnvironment;
import org.json.JSONArray;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.android.controller.ActivityController;
import static org.junit.Assert.*;

/** Real Activity, generated file listing, unbound store: no network or user documents. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk=34,application=android.app.Application.class,qualifiers="w800dp-h1200dp-mdpi")
@org.robolectric.annotation.GraphicsMode(org.robolectric.annotation.GraphicsMode.Mode.NATIVE)
public class HubFilesUiTest {
    private Button find(View root,String label) {
        if(root instanceof Button && label.contentEquals(((Button)root).getText()))return (Button)root;
        if(root instanceof ViewGroup)for(int i=0;i<((ViewGroup)root).getChildCount();i++){
            Button match=find(((ViewGroup)root).getChildAt(i),label);if(match!=null)return match;
        }
        return null;
    }
    private Object field(Object object,String name) throws Exception {java.lang.reflect.Field field=object.getClass().getDeclaredField(name);field.setAccessible(true);return field.get(object);}
    private void set(Object object,String name,Object value) throws Exception {java.lang.reflect.Field field=object.getClass().getDeclaredField(name);field.setAccessible(true);field.set(object,value);}
    private void invoke(Object object,String method) throws Exception {java.lang.reflect.Method m=object.getClass().getDeclaredMethod(method);m.setAccessible(true);m.invoke(object);}
    private void listing(HubFilesActivity activity,JSONArray entries) throws Exception {
        java.lang.reflect.Method render=HubFilesActivity.class.getDeclaredMethod("renderBrowse",JSONObject.class,String.class);render.setAccessible(true);
        render.invoke(activity,new JSONObject().put("path","").put("rootName","样本目录").put("entries",entries),"");
    }
    private JSONObject file(String name,long size) throws Exception {return new JSONObject().put("name",name).put("path",name).put("size",size);}
    private void snapshot(View root,String name) throws Exception {
        String target=System.getenv("STREAMHUB_RENDER_ROOT");if(target==null)return;
        java.io.File directory=new java.io.File(target);assertTrue(directory.isDirectory()||directory.mkdirs());
        android.graphics.Bitmap image=android.graphics.Bitmap.createBitmap(root.getWidth(),root.getHeight(),android.graphics.Bitmap.Config.ARGB_8888);
        root.draw(new android.graphics.Canvas(image));try(java.io.OutputStream out=new java.io.FileOutputStream(new java.io.File(directory,name))){image.compress(android.graphics.Bitmap.CompressFormat.PNG,100,out);}image.recycle();
    }
    @Test public void selectionBarTracksChecksSelectAllRefreshAndTabs() throws Exception {
        ActivityController<HubFilesActivity> controller=Robolectric.buildActivity(HubFilesActivity.class).setup();
        HubFilesActivity activity=controller.get();View root=activity.getWindow().getDecorView();
        assertEquals(View.GONE,((View)field(activity,"browserContent")).getVisibility());
        assertEquals(View.VISIBLE,((View)field(activity,"authorizationPane")).getVisibility());
        assertFalse(((View)field(activity,"sendButton")).isEnabled());
        assertEquals(View.GONE,((View)field(activity,"selectionBar")).getVisibility());
        ((View)((View)field(activity,"sendButton")).getTag()).performLongClick();
        assertTrue(org.robolectric.shadows.ShadowToast.getTextOfLatestToast().contains("“文件”未开启"));
        set(activity,"fileTransferAllowed",true);invoke(activity,"renderControls");
        JSONArray entries=new JSONArray().put(file("报告 中文 emoji 😀.txt",25)).put(file("sample.txt",125)).put(file("too-large.bin",Long.MAX_VALUE));
        listing(activity,entries);
        assertEquals(View.VISIBLE,((View)field(activity,"browserContent")).getVisibility());
        assertEquals(View.GONE,((View)field(activity,"authorizationPane")).getVisibility());
        java.util.List<CheckBox> checks=(java.util.List<CheckBox>)field(activity,"fileChecks");checks.get(0).performClick();
        assertEquals(View.VISIBLE,((View)field(activity,"selectionBar")).getVisibility());assertTrue(find(root,"下载（1）").isEnabled());
        find(root,"全选").performClick();assertNotNull(find(root,"下载（2）"));assertFalse(checks.get(2).isChecked());
        find(root,"传输任务").performClick();assertEquals(View.GONE,((View)field(activity,"selectionBar")).getVisibility());assertFalse(((View)field(activity,"sendButton")).isShown());
        find(root,"电脑文件").performClick();assertEquals(View.VISIBLE,((View)field(activity,"selectionBar")).getVisibility());
        // A changed listing cannot leave an invisible, stale file selected.
        listing(activity,new JSONArray().put(file("sample.txt",125)));assertNotNull(find(root,"下载（1）"));
        find(root,"取消").performClick();assertEquals(View.GONE,((View)field(activity,"selectionBar")).getVisibility());
        assertFalse(((java.util.List<CheckBox>)field(activity,"fileChecks")).get(0).isChecked());
        ((View)field(activity,"sortButton")).performClick();assertNotNull(org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog());
        controller.pause().stop().destroy();
    }
    @Test public void fileToolbarAndSelectionFitPhoneAndTabletInBothThemes() throws Exception {
        for(int[] size:new int[][]{{320,640},{1280,800}})for(int theme:new int[]{HubTheme.LIGHT,HubTheme.DARK})for(float scale:new float[]{1f,1.5f}){
            RuntimeEnvironment.setQualifiers("w"+size[0]+"dp-h"+size[1]+"dp-"+(size[0]>size[1]?"land":"port")+"-mdpi");
            RuntimeEnvironment.setFontScale(scale);
            HubTheme.setMode(RuntimeEnvironment.getApplication(),theme);
            try(ActivityController<HubFilesActivity> controller=Robolectric.buildActivity(HubFilesActivity.class).setup()){
                HubFilesActivity activity=controller.get();set(activity,"fileTransferAllowed",true);((TextView)field(activity,"state")).setText("已连接");invoke(activity,"renderControls");
                listing(activity,new JSONArray().put(file("报告.txt",25)).put(file("sample.txt",4096)));
                ((java.util.List<CheckBox>)field(activity,"fileChecks")).get(0).performClick();
                View root=((ViewGroup)activity.findViewById(android.R.id.content)).getChildAt(0);
                for(int i=0;i<3;i++){root.measure(View.MeasureSpec.makeMeasureSpec(size[0],View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(size[1],View.MeasureSpec.EXACTLY));root.layout(0,0,size[0],size[1]);}
                View send=(View)field(activity,"sendButton"),sort=(View)field(activity,"sortButton"),destination=(View)field(activity,"destinationButton"),refresh=(View)field(activity,"refreshButton");
                android.graphics.Rect a=new android.graphics.Rect(),b=new android.graphics.Rect(),c=new android.graphics.Rect();
                assertTrue(send.getGlobalVisibleRect(a));assertTrue(sort.getGlobalVisibleRect(b));assertTrue(destination.getGlobalVisibleRect(c));
                assertTrue(a.right<=size[0]);assertTrue(b.right<=c.left);assertTrue(c.right<=a.left);assertEquals(b.top,c.top);
                assertTrue(send.getHeight()>=48);assertTrue(sort.getHeight()>=48);
                android.graphics.Rect refreshBounds=new android.graphics.Rect();assertTrue(refresh.getGlobalVisibleRect(refreshBounds));assertTrue(refreshBounds.right<=b.left);assertEquals(b.top,refreshBounds.top);assertEquals(a.top,refreshBounds.top);
                assertTrue(find(root,"下载（1）").getGlobalVisibleRect(a));assertTrue(a.bottom<=size[1]);
                snapshot(root,"files-"+size[0]+"-theme"+theme+"-font"+scale+".png");
            }
        }
        RuntimeEnvironment.setFontScale(1f);
    }
    @Test public void emptyStatesAndRootChoicesUseOnePlace() throws Exception {
        try(ActivityController<HubFilesActivity> controller=Robolectric.buildActivity(HubFilesActivity.class).setup()){
            HubFilesActivity activity=controller.get();View root=activity.getWindow().getDecorView();
            assertEquals(View.GONE,((View)field(activity,"state")).getVisibility());
            assertTrue(((TextView)field(activity,"authorizationMessage")).getText().toString().contains("添加电脑"));
            find(root,"传输任务").performClick();assertEquals(View.VISIBLE,((View)field(activity,"transferEmpty")).getVisibility());assertEquals(View.GONE,((View)field(activity,"transferPane")).getVisibility());
            find(root,"电脑文件").performClick();assertEquals(View.GONE,((View)field(activity,"transferEmpty")).getVisibility());
            set(activity,"fileTransferAllowed",true);
            java.lang.reflect.Method renderRoots=HubFilesActivity.class.getDeclaredMethod("renderRoots",JSONArray.class);renderRoots.setAccessible(true);
            renderRoots.invoke(activity,new JSONArray().put(new JSONObject().put("id","one").put("name","Desktop")).put(new JSONObject().put("id","two").put("name","Downloads")));
            assertEquals(1,((ViewGroup)field(activity,"rootsList")).getChildCount());
            ((View)field(activity,"rootChoice")).performClick();android.app.AlertDialog choice=org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog();assertEquals(2,choice.getListView().getCount());choice.dismiss();
            renderRoots.invoke(activity,new JSONArray());
            java.lang.reflect.Method render=HubFilesActivity.class.getDeclaredMethod("renderBrowse",JSONObject.class,String.class);render.setAccessible(true);render.invoke(activity,null,"请在电脑的设备权限中添加可浏览目录。");invoke(activity,"renderControls");
            assertEquals(View.GONE,((View)field(activity,"state")).getVisibility());assertEquals(View.VISIBLE,((View)field(activity,"authorizationPane")).getVisibility());assertTrue(((TextView)field(activity,"authorizationMessage")).getText().toString().contains("添加可浏览目录"));
        }
    }
    @Test public void selectedLastRowCanScrollFullyAboveSelectionBarInLandscape() throws Exception {
        RuntimeEnvironment.setQualifiers("w1280dp-h800dp-land-mdpi");
        try(ActivityController<HubFilesActivity> controller=Robolectric.buildActivity(HubFilesActivity.class).setup()){
            HubFilesActivity activity=controller.get();set(activity,"fileTransferAllowed",true);invoke(activity,"renderControls");
            JSONArray entries=new JSONArray().put(new JSONObject().put("name","文件夹").put("path","folder").put("directory",true));for(int i=0;i<30;i++)entries.put(file("sample-"+i+".txt",38));listing(activity,entries);
            View root=((ViewGroup)activity.findViewById(android.R.id.content)).getChildAt(0);
            ((java.util.List<CheckBox>)field(activity,"fileChecks")).get(0).performClick();
            for(int i=0;i<4;i++){root.measure(View.MeasureSpec.makeMeasureSpec(1280,View.MeasureSpec.EXACTLY),View.MeasureSpec.makeMeasureSpec(800,View.MeasureSpec.EXACTLY));root.layout(0,0,1280,800);}
            android.widget.ScrollView scroll=(android.widget.ScrollView)field(activity,"contentScroll");scroll.scrollTo(0,Math.max(0,scroll.getChildAt(0).getHeight()-scroll.getHeight()+scroll.getPaddingTop()+scroll.getPaddingBottom()));
            ViewGroup rows=(ViewGroup)field(activity,"browseList");View last=rows.getChildAt(rows.getChildCount()-1),folder=rows.getChildAt(0);
            snapshot(root,"files-landscape-last-row.png");
            android.graphics.Rect visible=new android.graphics.Rect(),bar=new android.graphics.Rect();assertTrue("last row: scrollY="+scroll.getScrollY()+", viewport="+scroll.getHeight()+", body="+scroll.getChildAt(0).getHeight()+", rowTop="+last.getTop()+", rowBottom="+last.getBottom(),last.getGlobalVisibleRect(visible));assertTrue("selection bar is visible",((View)field(activity,"selectionBar")).getGlobalVisibleRect(bar));
            assertEquals(last.getHeight(),visible.height());assertTrue(visible.bottom<=bar.top);assertTrue(scroll.getPaddingBottom()>=((View)field(activity,"selectionBar")).getHeight());
            assertTrue(folder.getHeight()>=48&&folder.getHeight()<=64);assertTrue(last.getHeight()>=48&&last.getHeight()<=64);assertTrue(folder.getBackground() instanceof android.graphics.drawable.RippleDrawable);
            snapshot(root,"files-landscape-last-row.png");
        }
    }
    @Test public void firstDownloadExplainsDestinationBeforeOpeningSystemPicker() throws Exception {
        try(ActivityController<HubFilesActivity> controller=Robolectric.buildActivity(HubFilesActivity.class).setup()){
            HubFilesActivity activity=controller.get();set(activity,"fileTransferAllowed",true);invoke(activity,"renderControls");listing(activity,new JSONArray().put(file("sample.txt",38)));
            ((java.util.List<CheckBox>)field(activity,"fileChecks")).get(0).performClick();((View)field(activity,"downloadSelected")).performClick();
            assertNull(org.robolectric.Shadows.shadowOf(activity).getNextStartedActivityForResult());
            android.app.AlertDialog dialog=org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog();assertNotNull(dialog);assertEquals("选择保存位置",org.robolectric.Shadows.shadowOf(dialog).getTitle());
            dialog.getButton(android.content.DialogInterface.BUTTON_POSITIVE).performClick();
            org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle();
            assertEquals(android.content.Intent.ACTION_OPEN_DOCUMENT_TREE,org.robolectric.Shadows.shadowOf(activity).getNextStartedActivityForResult().intent.getAction());
        }
    }
    @Test public void documentPickerContextSurvivesRecreation() throws Exception {
        Bundle state=new Bundle();state.putString("pendingComputer","fixture-pc");state.putString("pendingRootId","root-two");state.putString("browseSort","size");
        state.putString("pendingBrowseDownloads",new JSONArray().put(new JSONObject().put("path","sample.txt")).toString());
        ActivityController<HubFilesActivity> controller=Robolectric.buildActivity(HubFilesActivity.class).create(state).start().resume().visible();
        Bundle saved=new Bundle();controller.saveInstanceState(saved);
        assertEquals("fixture-pc",saved.getString("pendingComputer"));assertEquals("root-two",saved.getString("pendingRootId"));
        assertEquals("size",saved.getString("browseSort"));assertEquals(1,new JSONArray(saved.getString("pendingBrowseDownloads")).length());
        controller.pause().stop().destroy();
    }
    @Test public void savedDestinationRequiresPersistedWritePermissionAndAnExistingDirectory() throws Exception {
        final boolean[] exists={true};
        android.content.ContentProvider provider=new android.content.ContentProvider() {
            public boolean onCreate(){return true;}
            public String getType(android.net.Uri uri){return android.provider.DocumentsContract.Document.MIME_TYPE_DIR;}
            public android.net.Uri insert(android.net.Uri uri,android.content.ContentValues values){throw new UnsupportedOperationException();}
            public int delete(android.net.Uri uri,String selection,String[] args){throw new UnsupportedOperationException();}
            public int update(android.net.Uri uri,android.content.ContentValues values,String selection,String[] args){throw new UnsupportedOperationException();}
            public android.database.Cursor query(android.net.Uri uri,String[] projection,String selection,String[] args,String sort){
                android.database.MatrixCursor cursor=new android.database.MatrixCursor(projection);
                if(exists[0]){
                    Object[] row=new Object[projection.length];
                    for(int i=0;i<projection.length;i++)switch(projection[i]){
                        case android.provider.DocumentsContract.Document.COLUMN_MIME_TYPE:row[i]=android.provider.DocumentsContract.Document.MIME_TYPE_DIR;break;
                        case android.provider.DocumentsContract.Document.COLUMN_FLAGS:row[i]=android.provider.DocumentsContract.Document.FLAG_DIR_SUPPORTS_CREATE|android.provider.DocumentsContract.Document.FLAG_SUPPORTS_WRITE;break;
                        default:row[i]="receives";
                    }
                    cursor.addRow(row);
                }
                return cursor;
            }
        };
        org.robolectric.shadows.ShadowContentResolver.registerProviderInternal("fixture.documents",provider);
        ActivityController<HubFilesActivity> controller=Robolectric.buildActivity(HubFilesActivity.class).setup();
        HubFilesActivity activity=controller.get();
        android.net.Uri uri=android.net.Uri.parse("content://fixture.documents/tree/receives");
        activity.getSharedPreferences("hub_file_preferences",0).edit().putString("downloadTree",uri.toString()).commit();
        java.lang.reflect.Method saved=HubFilesActivity.class.getDeclaredMethod("savedDestination");saved.setAccessible(true);
        assertNull(saved.invoke(activity));
        activity.getContentResolver().takePersistableUriPermission(uri,android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION);
        assertNull(saved.invoke(activity));
        activity.getContentResolver().takePersistableUriPermission(uri,android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        assertEquals(uri,saved.invoke(activity));
        exists[0]=false;assertNull(saved.invoke(activity));
        exists[0]=true;activity.getContentResolver().releasePersistableUriPermission(uri,android.content.Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        assertNull(saved.invoke(activity));
        controller.pause().stop().destroy();
    }
}
