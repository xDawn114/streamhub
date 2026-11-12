package com.limelight.streamhub;

import android.content.ClipData;
import android.content.Context;
import android.net.Uri;
import android.os.Build;
import android.text.Html;
import android.util.Base64;
import androidx.core.content.FileProvider;
import org.json.JSONObject;
import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;

/** One explicit text, HTML or PNG clipboard transfer; no background polling. */
public final class HubClipboard {
    public static final int MAX_BYTES = 1024 * 1024;
    private HubClipboard() { }

    public static JSONObject capture(Context context, ClipData clip) throws Exception {
        if (clip == null || clip.getItemCount() == 0) throw new IOException("当前剪贴板没有可发送的内容。");
        ClipData.Item item = clip.getItemAt(0);
        String format;
        byte[] bytes;
        if (item.getUri() != null && clip.getDescription().hasMimeType("image/png")) {
            format = "image/png";
            try (InputStream input = context.getContentResolver().openInputStream(item.getUri())) {
                if (input == null) throw new IOException("无法读取剪贴板图片。");
                ByteArrayOutputStream output = new ByteArrayOutputStream();
                byte[] buffer = new byte[8192]; int count;
                while ((count = input.read(buffer)) != -1) {
                    if (output.size() + count > MAX_BYTES) throw new IOException("剪贴板内容超过 1 MiB。");
                    output.write(buffer, 0, count);
                }
                bytes = output.toByteArray();
            }
        } else if (item.getHtmlText() != null) {
            format = "text/html"; bytes = item.getHtmlText().getBytes(StandardCharsets.UTF_8);
        } else if (item.getText() != null) {
            format = "text/plain"; bytes = item.getText().toString().getBytes(StandardCharsets.UTF_8);
        } else throw new IOException("剪贴板只支持文字、HTML 和 PNG 图片。");
        validate(format, bytes);
        return new JSONObject().put("format", format).put("data", Base64.encodeToString(bytes, Base64.NO_WRAP));
    }

    public static ClipData restore(Context context, JSONObject payload) throws Exception {
        if (payload == null) throw new IOException("电脑没有返回剪贴板内容。");
        String format = payload.getString("format"), encoded = payload.getString("data");
        if (encoded.length() > ((MAX_BYTES + 2) / 3) * 4 || encoded.length() % 4 != 0 ||
                !encoded.matches("[A-Za-z0-9+/]*={0,2}")) throw new IOException("电脑剪贴板内容编码无效。");
        byte[] bytes;
        try { bytes = Base64.decode(encoded, Base64.NO_WRAP); }
        catch (IllegalArgumentException error) { throw new IOException("电脑剪贴板内容无效。", error); }
        if (!Base64.encodeToString(bytes, Base64.NO_WRAP).equals(encoded))
            throw new IOException("电脑剪贴板内容编码无效。");
        validate(format, bytes);
        if ("image/png".equals(format)) {
            File directory = new File(context.getCacheDir(), "streamhub-clipboard");
            if (!directory.isDirectory() && !directory.mkdirs()) throw new IOException("无法保存剪贴板图片。");
            File image = new File(directory, "current.png");
            try (FileOutputStream output = new FileOutputStream(image)) { output.write(bytes); }
            Uri uri = FileProvider.getUriForFile(context, context.getPackageName() + ".fileprovider", image);
            return ClipData.newUri(context.getContentResolver(), "我控制", uri);
        }
        String text = new String(bytes, StandardCharsets.UTF_8);
        if ("text/html".equals(format)) {
            CharSequence plain = Build.VERSION.SDK_INT >= 24 ? Html.fromHtml(text, Html.FROM_HTML_MODE_LEGACY) : Html.fromHtml(text);
            return ClipData.newHtmlText("我控制", plain, text);
        }
        return ClipData.newPlainText("我控制", text);
    }

    private static void validate(String format, byte[] bytes) throws IOException {
        if (bytes.length == 0 || bytes.length > MAX_BYTES) throw new IOException("剪贴板内容为空或超过 1 MiB。");
        if ("image/png".equals(format)) {
            byte[] signature = {(byte)137, 80, 78, 71, 13, 10, 26, 10};
            if (bytes.length < 24 || bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R')
                throw new IOException("剪贴板图片不是有效的 PNG。");
            for (int i=0; i<signature.length; i++) if (bytes[i] != signature[i]) throw new IOException("剪贴板图片不是有效的 PNG。");
            long width = uint32(bytes, 16), height = uint32(bytes, 20);
            if (width == 0 || height == 0 || width > 16384 || height > 16384 || width * height > 40000000L)
                throw new IOException("剪贴板图片尺寸过大。");
        } else if ("text/plain".equals(format) || "text/html".equals(format)) {
            try { StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT).decode(java.nio.ByteBuffer.wrap(bytes)); }
            catch (CharacterCodingException error) { throw new IOException("剪贴板文字编码无效。", error); }
        } else {
            throw new IOException("电脑剪贴板格式不支持。");
        }
    }
    private static long uint32(byte[] bytes, int offset) {
        return ((long)(bytes[offset] & 0xFF) << 24) | ((long)(bytes[offset + 1] & 0xFF) << 16) |
                ((long)(bytes[offset + 2] & 0xFF) << 8) | (bytes[offset + 3] & 0xFFL);
    }
}
