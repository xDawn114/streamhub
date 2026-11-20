package com.limelight.streamhub;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.view.View;
import android.widget.Button;
import androidx.core.graphics.ColorUtils;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.GraphicsMode;
import static org.junit.Assert.*;

@RunWith(RobolectricTestRunner.class)
@Config(sdk=34, application=android.app.Application.class, qualifiers="mdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public class HubThemeControlsTest {
    @Test public void bothThemesKeepTextAndControlBoundariesReadable() {
        Context context = RuntimeEnvironment.getApplication();
        for (int theme : new int[]{HubTheme.LIGHT, HubTheme.DARK}) {
            HubTheme.setMode(context, theme);
            for (int fill : new int[]{HubTheme.primary(context), HubTheme.primaryHover(context), HubTheme.primaryPressed(context)})
                assertTrue(ColorUtils.calculateContrast(Color.WHITE, fill) >= 4.5);
            for (int fill : new int[]{HubTheme.surface(context), HubTheme.field(context), HubTheme.hover(context), HubTheme.pressed(context)})
                assertTrue(ColorUtils.calculateContrast(HubTheme.controlBorder(context), fill) >= 3);
            for (boolean primary : new boolean[]{true, false}) {
                Button button = new Button(context); HubButtons.apply(button, primary); button.setEnabled(false);
                assertTrue(ColorUtils.calculateContrast(button.getCurrentTextColor(), HubTheme.disabled(context)) >= 3);
                assertEquals(1f, button.getAlpha(), 0f);
                assertTrue(button.getMinimumHeight() >= 48);
            }
        }
    }

    @Test public void primarySelectedStateKeepsWhiteTextOnBlue() {
        Context context = RuntimeEnvironment.getApplication();
        for (int theme : new int[]{HubTheme.LIGHT, HubTheme.DARK}) {
            HubTheme.setMode(context, theme);
            Button button = new Button(context); HubButtons.apply(button, true); button.setSelected(true);
            Bitmap image = render(button, 180, 48);
            assertEquals(HubTheme.primaryPressed(context), image.getPixel(12, 24));
            assertEquals(Color.WHITE, button.getCurrentTextColor());
            image.recycle();
        }
    }

    @Test public void switchesKeepBothThumbPositionsAndNativeClickBehavior() {
        Context context = RuntimeEnvironment.getApplication();
        for (int theme : new int[]{HubTheme.LIGHT, HubTheme.DARK}) {
            HubTheme.setMode(context, theme);
            HubSwitch toggle = new HubSwitch(context);
            int[] changed = {0};
            toggle.setOnCheckedChangeListener((button, checked) -> changed[0]++);
            assertFalse(toggle.isChecked());
            Bitmap off = render(toggle, 100, 48);
            assertEquals(Color.WHITE, off.getPixel(62, 24));
            toggle.performClick(); assertTrue(toggle.isChecked()); assertEquals(1, changed[0]);
            Bitmap on = render(toggle, 100, 48);
            assertEquals(Color.WHITE, on.getPixel(86, 24));
            assertEquals(HubTheme.primary(context), on.getPixel(62, 24));
            off.recycle(); on.recycle();
        }
    }

    private Bitmap render(View view, int width, int height) {
        view.measure(View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY),
                View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY));
        view.layout(0, 0, width, height);
        Bitmap image = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
        view.draw(new Canvas(image));
        return image;
    }
}
