package com.limelight.streamhub;

import android.app.Application;
import org.json.JSONObject;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import java.io.File;
import java.io.FileOutputStream;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28,manifest=Config.NONE,application=Application.class)
public class HubFileTasksTest {
    @Test public void queuePreservesSelectionOrderAndSkipsFailedAndOtherComputerTasks() throws Exception {
        HubFileTasks store=new HubFileTasks(RuntimeEnvironment.getApplication());
        JSONObject a=store.create("one","download","content://folder","a.txt",0,"","");a.put("autoQueued",true);store.save(a);
        JSONObject b=store.create("one","download","content://folder","b.txt",0,"","");b.put("autoQueued",true);store.save(b);
        JSONObject other=store.create("two","download","content://folder","other.txt",0,"","");other.put("autoQueued",true);store.save(other);
        assertEquals(a.getString("id"),store.nextQueued("one").getString("id"));
        a.put("attempted",true).put("state","paused");store.save(a);
        assertEquals(b.getString("id"),store.nextQueued("one").getString("id"));
        b.put("state","cancelled");store.save(b);
        assertNull(store.nextQueued("one"));assertNotNull(store.nextQueued("two"));
    }
    @Test public void progressAndDocumentGrantReferenceSurviveRecreation() throws Exception {
        Application app=RuntimeEnvironment.getApplication(); HubFileTasks store=new HubFileTasks(app);
        JSONObject task=store.create("computer-one","download","content://folder/selected","测试.bin",37,"remote-task-1",new String(new char[64]).replace('\0','A'));
        task.put("offset",12).put("state","paused");store.save(task);
        JSONObject loaded=new HubFileTasks(app).read(task.getString("id"));
        assertEquals(12,loaded.getLong("offset"));assertEquals("content://folder/selected",loaded.getString("uri"));assertEquals("computer-one",loaded.getString("computer"));
    }
    @Test public void traversalAndOversizedTasksNeverCreateRecords() throws Exception {
        HubFileTasks store=new HubFileTasks(RuntimeEnvironment.getApplication());int before=store.list().size();
        assertTrue(HubFileTasks.isSizeAllowed(HubFileTasks.MAX_BYTES));
        assertFalse(HubFileTasks.isSizeAllowed(HubFileTasks.MAX_BYTES + 1));
        assertFalse(HubFileTasks.isSizeAllowed(-1));
        for(String name:new String[]{"../bad.bin","a/b.bin","CON.txt","COM¹.txt","a."})assertFalse(HubFileTasks.safeName(name));
        try{store.create("computer-one","upload","content://selected","big.bin",HubFileTasks.MAX_BYTES+1,"","");fail("oversize accepted");}catch(java.io.IOException expected){}
        assertEquals(before,store.list().size());assertTrue(HubFileTasks.safeName("照片 01.png"));
    }
    @Test public void historyCleanupKeepsActiveTasksAndUserFiles() throws Exception {
        Application app=RuntimeEnvironment.getApplication();HubFileTasks store=new HubFileTasks(app);
        JSONObject completed=store.create("computer-one","download","content://selected","done.bin",1,"remote-task-1","");
        JSONObject active=store.create("computer-one","upload","content://selected","active.bin",1,"","");
        String id=completed.getString("id");try(FileOutputStream output=new FileOutputStream(store.data(id))){output.write(1);}
        File userFile=new File(app.getFilesDir(),"user-received.txt");try(FileOutputStream output=new FileOutputStream(userFile)){output.write(7);}
        completed.put("state","completed");store.save(completed);store.clearFinished();
        assertFalse(store.data(id).exists());assertTrue(userFile.exists());assertNotNull(store.read(active.getString("id")));
    }
}
