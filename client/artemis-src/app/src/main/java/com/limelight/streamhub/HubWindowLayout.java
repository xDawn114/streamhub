package com.limelight.streamhub;

import android.content.Context;
import android.content.res.Configuration;
import android.view.View;
import android.widget.LinearLayout;

/** Window classes, not device models: also applies to tablet split-screen windows. */
final class HubWindowLayout {
    final int widthDp, heightDp;
    final float fontScale;

    private HubWindowLayout(Configuration configuration) {
        widthDp = configuration.screenWidthDp;
        heightDp = configuration.screenHeightDp;
        fontScale = Math.max(1f, configuration.fontScale);
    }

    static HubWindowLayout of(Context context) {
        return new HubWindowLayout(context.getResources().getConfiguration());
    }

    boolean compact() { return Math.min(widthDp, heightDp) < 600; }
    int railWidthDp() {
        if (widthDp <= heightDp || widthDp < 600) return 0;
        return compact() ? 80 : 132;
    }
    int bottomHeightDp() { return Math.max(compact() ? 72 : 80, 52 + (int)Math.ceil(18 * fontScale)); }
    int pageInsetDp() { return compact() ? 16 : 20; }
    int cardInsetDp() { return compact() ? 16 : 24; }
    int contentWidthDp() { return Math.max(1, Math.min(720, widthDp - railWidthDp())); }

    /** Reflows based on the row's actual measured width, including nested card padding. */
    static final class ActionRow extends LinearLayout {
        private final int minimumCell, gap;
        ActionRow(Context context, int minimumCellDp, int gapDp) {
            super(context);
            float density = getResources().getDisplayMetrics().density;
            minimumCell = Math.round(minimumCellDp * density);
            gap = Math.round(gapDp * density);
            setBaselineAligned(false);
        }
        @Override protected void onMeasure(int widthSpec, int heightSpec) {
            int count = 0;
            for (int i = 0; i < getChildCount(); i++) if (getChildAt(i).getVisibility() != View.GONE) count++;
            int width = MeasureSpec.getSize(widthSpec) - getPaddingLeft() - getPaddingRight();
            float scale = Math.max(1f, getResources().getConfiguration().fontScale);
            boolean stacked = MeasureSpec.getMode(widthSpec) != MeasureSpec.UNSPECIFIED
                    && width < Math.round(count * minimumCell * scale) + Math.max(0, count - 1) * gap;
            int direction = stacked ? VERTICAL : HORIZONTAL;
            if (getOrientation() != direction) setOrientation(direction);
            int visible = 0;
            for (int i = 0; i < getChildCount(); i++) {
                View child = getChildAt(i);
                if (child.getVisibility() == View.GONE) continue;
                LayoutParams size = (LayoutParams)child.getLayoutParams();
                int targetWidth = stacked ? LayoutParams.MATCH_PARENT : 0;
                int left = !stacked && visible > 0 ? gap : 0;
                int top = stacked && visible > 0 ? gap : 0;
                float weight = stacked ? 0 : 1;
                if (size.width != targetWidth || size.height != LayoutParams.WRAP_CONTENT || size.weight != weight
                        || size.leftMargin != left || size.topMargin != top) {
                    size.width = targetWidth; size.height = LayoutParams.WRAP_CONTENT; size.weight = weight;
                    size.leftMargin = left; size.topMargin = top;
                    child.setLayoutParams(size);
                }
                visible++;
            }
            super.onMeasure(widthSpec, heightSpec);
        }
    }
}
