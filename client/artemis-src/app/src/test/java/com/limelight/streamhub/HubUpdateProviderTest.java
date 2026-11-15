package com.limelight.streamhub;

import android.content.Context;
import android.content.pm.ProviderInfo;
import android.net.Uri;
import androidx.core.content.FileProvider;
import java.io.File;
import java.nio.file.Files;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk = 34, application = android.app.Application.class)
public class HubUpdateProviderTest {
    @Test public void updateAndClipboardProvidersHaveSeparateAndroidComponents() {
        Context context = RuntimeEnvironment.getApplication();
        ProviderInfo updates = context.getPackageManager().resolveContentProvider(context.getPackageName() + ".updates", 0);
        ProviderInfo clipboard = context.getPackageManager().resolveContentProvider(context.getPackageName() + ".fileprovider", 0);
        assertNotNull(updates); assertNotNull(clipboard);
        assertNotEquals("Android caches local providers by component name", clipboard.name, updates.name);
        assertEquals("com.limelight.streamhub.HubUpdateFileProvider", updates.name);
        assertFalse(updates.exported); assertTrue(updates.grantUriPermissions);
    }

    @Test public void updateUriReadsTheCompletePackageAndRejectsOtherPrivateFiles() throws Exception {
        // AndroidX builds URI roots with '/', while Windows-hosted Robolectric
        // returns '\\' canonical file paths. Exercise this on Android/Unix instead.
        org.junit.Assume.assumeFalse(System.getProperty("os.name", "").startsWith("Windows"));
        Context context = RuntimeEnvironment.getApplication();
        File directory = new File(context.getFilesDir(), "updates/provider-regression");
        assertTrue(directory.mkdirs() || directory.isDirectory());
        File apk = new File(directory, "StreamHub-update.apk");
        byte[] bytes = new byte[]{80, 75, 3, 4, 1, 2, 3};
        Files.write(apk.toPath(), bytes);
        Uri uri = FileProvider.getUriForFile(context, context.getPackageName() + ".updates", apk);
        try (java.io.InputStream input = context.getContentResolver().openInputStream(uri)) {
            assertNotNull(input); assertArrayEquals(bytes, input.readAllBytes());
        }
        File outside = new File(context.getFilesDir(), "unrelated-private-file");
        Files.write(outside.toPath(), bytes);
        assertThrows(IllegalArgumentException.class, () -> FileProvider.getUriForFile(context, context.getPackageName() + ".updates", outside));
    }
}
