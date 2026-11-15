package com.limelight.utils;

import android.content.Context;
import android.graphics.Matrix;
import android.view.GestureDetector;
import android.view.MotionEvent;
import android.view.ScaleGestureDetector;
import android.view.View;

import com.limelight.Game;
import com.limelight.preferences.PreferenceConfiguration;

/** Local view transform only. This class never sends remote input. */
public class PanZoomHandler {
    private final Game game;
    private final View streamView;
    private final boolean isTopMode;
    private final float maxScale;
    private final ScaleGestureDetector scaleGestureDetector;
    private final GestureDetector gestureDetector;
    private View parent;
    private float scaleFactor = 1f, childX, childY;
    private float parentWidth, parentHeight, childWidth, childHeight;
    private Runnable changed;

    public PanZoomHandler(Context context, Game game, View streamView, PreferenceConfiguration prefConfig) {
        this.game = game;
        this.streamView = streamView;
        isTopMode = prefConfig.alignDisplayTopCenter;
        maxScale = game.isHubManagedSession() ? 4f : 10f;
        scaleGestureDetector = new ScaleGestureDetector(context, new ScaleListener());
        scaleGestureDetector.setQuickScaleEnabled(false);
        gestureDetector = new GestureDetector(context, new GestureListener());
        streamView.setPivotX(0);
        streamView.setPivotY(0);
    }

    public void setOnViewportChangedListener(Runnable listener) { changed = listener; }

    public void handleTouchEvent(MotionEvent event) { handleTouchEvent(null, event); }

    public void handleTouchEvent(View source, MotionEvent event) {
        if (!dimensions()) return;
        // Android may already have inverse-transformed an event delivered to the
        // SurfaceView. Gesture distances/foci must use its parent's stable space.
        MotionEvent local = MotionEvent.obtain(event);
        try {
            if (source != null && source != parent && source.getParent() == parent) {
                Matrix matrix = new Matrix(source.getMatrix());
                matrix.postTranslate(source.getLeft() - parent.getScrollX(), source.getTop() - parent.getScrollY());
                local.transform(matrix);
            }
            scaleGestureDetector.onTouchEvent(local);
            gestureDetector.onTouchEvent(local);
        } finally { local.recycle(); }
    }

    public void cancelGesture() {
        long now = android.os.SystemClock.uptimeMillis();
        MotionEvent cancel = MotionEvent.obtain(now, now, MotionEvent.ACTION_CANCEL, 0, 0, 0);
        try { scaleGestureDetector.onTouchEvent(cancel); gestureDetector.onTouchEvent(cancel); }
        finally { cancel.recycle(); }
    }

    private boolean dimensions() {
        if (!(streamView.getParent() instanceof View)) return false;
        parent = (View) streamView.getParent();
        parentWidth = parent.getWidth(); parentHeight = parent.getHeight();
        childWidth = streamView.getWidth() * scaleFactor;
        childHeight = streamView.getHeight() * scaleFactor;
        return parentWidth > 0 && parentHeight > 0 && childWidth > 0 && childHeight > 0;
    }

    private void apply() {
        if (dimensions()) {
            childX = parentWidth >= childWidth ? (parentWidth - childWidth) / 2f
                    : Math.max(parentWidth - childWidth, Math.min(childX, 0));
            childY = parentHeight >= childHeight ? (isTopMode ? 0 : (parentHeight - childHeight) / 2f)
                    : Math.max(parentHeight - childHeight, Math.min(childY, 0));
        }
        streamView.setScaleX(scaleFactor); streamView.setScaleY(scaleFactor);
        streamView.setX(childX); streamView.setY(childY);
        if (changed != null) changed.run();
    }

    public void handleSurfaceChange() {
        float oldWidth = childWidth, oldHeight = childHeight;
        float oldParentWidth = parentWidth, oldParentHeight = parentHeight;
        if (!dimensions()) return;
        if (oldWidth > 0 && oldHeight > 0) {
            childX = (childX - oldParentWidth / 2f) * childWidth / oldWidth + parentWidth / 2f;
            childY = (childY - oldParentHeight / 2f) * childHeight / oldHeight + parentHeight / 2f;
        }
        apply();
    }

    private void scaleAt(float desired, float focusX, float focusY, float previousX, float previousY) {
        float next = Float.isNaN(desired) || Float.isInfinite(desired) ? 1f : Math.max(1f, Math.min(maxScale, desired));
        float ratio = next / scaleFactor;
        childX = focusX - (previousX - childX) * ratio;
        childY = focusY - (previousY - childY) * ratio;
        scaleFactor = next;
        apply();
    }

    public void scaleBy(float factor) {
        if (!dimensions() || factor <= 0 || Float.isNaN(factor) || Float.isInfinite(factor)) return;
        cancelGesture();
        scaleAt(scaleFactor * factor, parentWidth / 2f, parentHeight / 2f, parentWidth / 2f, parentHeight / 2f);
        game.updatePipAutoEnter();
    }

    public void reset() {
        cancelGesture();
        setInitialZoomAndPan(1f, 0, 0);
        game.updatePipAutoEnter();
    }

    private class ScaleListener extends ScaleGestureDetector.SimpleOnScaleGestureListener {
        float previousX, previousY;
        @Override public boolean onScaleBegin(ScaleGestureDetector detector) {
            previousX = detector.getFocusX(); previousY = detector.getFocusY(); return true;
        }
        @Override public boolean onScale(ScaleGestureDetector detector) {
            scaleAt(scaleFactor * detector.getScaleFactor(), detector.getFocusX(), detector.getFocusY(), previousX, previousY);
            previousX = detector.getFocusX(); previousY = detector.getFocusY(); return true;
        }
        @Override public void onScaleEnd(ScaleGestureDetector detector) { game.updatePipAutoEnter(); }
    }

    private class GestureListener extends GestureDetector.SimpleOnGestureListener {
        @Override public boolean onDown(MotionEvent event) { return true; }
        @Override public boolean onScroll(MotionEvent first, MotionEvent event, float dx, float dy) {
            if (scaleGestureDetector.isInProgress() || event.getPointerCount() != 1) return true;
            childX -= dx; childY -= dy; apply(); return true;
        }
    }

    public void setInitialZoomAndPan(float scale, float x, float y) {
        scaleFactor = Float.isNaN(scale) || Float.isInfinite(scale) ? 1f : Math.max(1f, Math.min(maxScale, scale));
        childX = Float.isNaN(x) || Float.isInfinite(x) ? 0 : x;
        childY = Float.isNaN(y) || Float.isInfinite(y) ? 0 : y;
        apply();
    }
    public float getScaleFactor() { return scaleFactor; }
    public float getChildX() { return childX; }
    public float getChildY() { return childY; }
}
