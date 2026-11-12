package com.limelight.streamhub;

import android.content.ContentResolver;
import android.database.Cursor;
import android.net.Uri;
import android.provider.DocumentsContract;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.util.ArrayList;
import java.util.List;

final class HubSafDocuments implements HubFilePublication.Documents {
    private final ContentResolver resolver;
    private final Uri tree, parent;

    HubSafDocuments(ContentResolver resolver, Uri tree) {
        this.resolver = resolver; this.tree = tree;
        parent = DocumentsContract.buildDocumentUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree));
    }
    @Override public List<String> find(String name) throws Exception {
        Uri children = DocumentsContract.buildChildDocumentsUriUsingTree(tree, DocumentsContract.getTreeDocumentId(tree));
        List<String> matches = new ArrayList<>();
        try (Cursor cursor = resolver.query(children, new String[]{DocumentsContract.Document.COLUMN_DOCUMENT_ID,
                DocumentsContract.Document.COLUMN_DISPLAY_NAME}, null, null, null)) {
            if (cursor == null) throw new IOException("无法检查接收文件夹。");
            while (cursor.moveToNext()) if (name.equals(cursor.getString(1)))
                matches.add(DocumentsContract.buildDocumentUriUsingTree(tree, cursor.getString(0)).toString());
        }
        return matches;
    }
    @Override public String name(String uri) throws Exception {
        try (Cursor cursor = resolver.query(Uri.parse(uri), new String[]{DocumentsContract.Document.COLUMN_DISPLAY_NAME}, null, null, null)) {
            if (cursor == null || !cursor.moveToFirst()) return null;
            return cursor.getString(0);
        }
    }
    @Override public String create(String name) throws Exception {
        Uri result = DocumentsContract.createDocument(resolver, parent, "application/octet-stream", name);
        return result == null ? null : result.toString();
    }
    @Override public InputStream read(String uri) throws Exception { return resolver.openInputStream(Uri.parse(uri)); }
    @Override public OutputStream write(String uri) throws Exception { return resolver.openOutputStream(Uri.parse(uri), "w"); }
    @Override public String rename(String uri, String name) throws Exception {
        Uri result = DocumentsContract.renameDocument(resolver, Uri.parse(uri), name);
        return result == null ? null : result.toString();
    }
}
