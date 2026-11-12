package com.limelight.streamhub;

import android.app.Application;
import android.content.ClipData;
import android.content.ClipDescription;
import android.net.Uri;
import android.util.Base64;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import org.json.JSONObject;
import org.junit.Rule;
import org.junit.Test;
import org.junit.rules.TemporaryFolder;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=28, manifest=Config.NONE, application=Application.class)
public class HubClipboardBoundaryTest {
    @Rule public TemporaryFolder files = new TemporaryFolder();
    private final Application app = RuntimeEnvironment.getApplication();

    @Test public void unicodeTextFitsByUtf8BytesAndRejectsOversize() throws Exception {
        char[] chars = new char[HubClipboard.MAX_BYTES / 3]; Arrays.fill(chars, '中');
        String text = new String(chars) + "a";
        assertEquals(HubClipboard.MAX_BYTES, text.getBytes(StandardCharsets.UTF_8).length);
        JSONObject payload = HubClipboard.capture(app, ClipData.newPlainText("test", text));
        assertEquals(text, HubClipboard.restore(app, payload).getItemAt(0).getText().toString());
        try { HubClipboard.capture(app, ClipData.newPlainText("test", text + "a")); fail("oversize accepted"); }
        catch (IOException expected) { assertTrue(expected.getMessage().contains("1 MiB")); }
    }
    @Test public void repeatedPlainAndHtmlActionsDoNotReusePreviousContent() throws Exception {
        for (int i = 0; i < 12; i++) {
            String plain = "第" + i + "次";
            String html = "<b>" + plain + "</b>";
            ClipData source = i % 2 == 0 ? ClipData.newPlainText("test", plain) : ClipData.newHtmlText("test", plain, html);
            ClipData restored = HubClipboard.restore(app, HubClipboard.capture(app, source));
            if (i % 2 == 0) assertEquals(plain, restored.getItemAt(0).getText().toString());
            else assertEquals(html, restored.getItemAt(0).getHtmlText());
        }
    }
    @Test public void emojiAndHtmlPreserveUtf8Boundary() throws Exception {
        StringBuilder value = new StringBuilder(HubClipboard.MAX_BYTES / 2);
        for (int i=0; i<HubClipboard.MAX_BYTES/4; i++) value.append("😀");
        String text=value.toString();
        JSONObject plain=HubClipboard.capture(app,ClipData.newPlainText("test",text));
        assertEquals(text,HubClipboard.restore(app,plain).getItemAt(0).getText().toString());
        String html="<p>中文 😀 &amp; <b>格式</b></p>";
        assertEquals(html,HubClipboard.restore(app,HubClipboard.capture(app,ClipData.newHtmlText("test","中文 😀 & 格式",html)))
                .getItemAt(0).getHtmlText());
        try { HubClipboard.capture(app,ClipData.newPlainText("test",text+"a"));fail("emoji payload over limit accepted"); }
        catch(IOException expected) { assertTrue(expected.getMessage().contains("1 MiB")); }
    }
    @Test public void malformedUtf8AndNoncanonicalBase64AreRejected() throws Exception {
        for(String encoded:new String[]{"Zh==","Zg=", "Zg==\n", " Zg==", "====", "wK8=", "7aCA", ""}) {
            try { HubClipboard.restore(app,new JSONObject().put("format","text/plain").put("data",encoded));
                fail("invalid wire text accepted: "+encoded); }
            catch(IOException expected) { }
        }
        assertEquals("f",HubClipboard.restore(app,new JSONObject().put("format","text/plain").put("data","Zg=="))
                .getItemAt(0).getText().toString());
    }
    @Test public void pngCaptureRejectsOversizeAndMalformedContent() throws Exception {
        byte[] valid = Base64.decode("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a3sAAAAASUVORK5CYII=", Base64.DEFAULT);
        assertEquals("image/png", HubClipboard.capture(app, image(valid)).getString("format"));
        try { HubClipboard.capture(app, image(new byte[24])); fail("invalid PNG accepted"); }
        catch (IOException expected) { assertTrue(expected.getMessage().contains("PNG")); }
        byte[] oversized = Arrays.copyOf(valid, HubClipboard.MAX_BYTES + 1);
        try { HubClipboard.capture(app, image(oversized)); fail("oversize PNG accepted"); }
        catch (IOException expected) { assertTrue(expected.getMessage().contains("1 MiB")); }
        byte[] huge = Arrays.copyOf(valid, valid.length);
        huge[16] = 0x00; huge[17] = 0x00; huge[18] = 0x50; huge[19] = 0x01;
        huge[20] = 0x00; huge[21] = 0x00; huge[22] = 0x50; huge[23] = 0x01;
        try { HubClipboard.restore(app, new JSONObject().put("format", "image/png")
                .put("data", Base64.encodeToString(huge, Base64.NO_WRAP))); fail("oversized PNG dimensions accepted"); }
        catch (IOException expected) { assertTrue(expected.getMessage().contains("尺寸")); }
        try { HubClipboard.restore(app, new JSONObject().put("format", "text/plain").put("data", "!!!!")); fail("invalid base64 accepted"); }
        catch (IOException expected) { assertTrue(expected.getMessage().contains("编码")); }
    }
    private ClipData image(byte[] bytes) throws Exception {
        File file = files.newFile();
        try (FileOutputStream output = new FileOutputStream(file)) { output.write(bytes); }
        return new ClipData(new ClipDescription("test", new String[]{"image/png"}), new ClipData.Item(Uri.fromFile(file)));
    }
}
