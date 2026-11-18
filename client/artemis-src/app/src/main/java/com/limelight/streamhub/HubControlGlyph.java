package com.limelight.streamhub;

import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.PixelFormat;
import android.graphics.drawable.Drawable;

/** Small line icons share one stroke and remain independent of font glyph coverage. */
public final class HubControlGlyph extends Drawable {
    private final String kind;
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private boolean warning;
    public HubControlGlyph(String kind, int color) {
        this.kind=kind; paint.setColor(color); paint.setStrokeWidth(1.8f);
        paint.setStrokeCap(Paint.Cap.ROUND); paint.setStrokeJoin(Paint.Join.ROUND);
    }
    public void warning(boolean value) { warning=value; invalidateSelf(); }
    @Override public void draw(Canvas canvas) {
        canvas.save(); canvas.translate(getBounds().left,getBounds().top);
        canvas.scale(getBounds().width()/24f,getBounds().height()/24f);
        paint.setStyle(Paint.Style.STROKE);
        switch(kind) {
            case "exit":
                canvas.drawLine(5,5,19,19,paint); canvas.drawLine(19,5,5,19,paint); break;
            case "rotate":
                canvas.drawRoundRect(6,3,18,21,2,2,paint);canvas.drawLine(2,8,2,3,paint);
                canvas.drawLine(2,3,7,3,paint);canvas.drawArc(1,1,23,23,200,85,false,paint);break;
            case "audio":
                canvas.drawRect(3,9,7,15,paint);canvas.drawLine(7,9,12,5,paint);
                canvas.drawLine(12,5,12,19,paint);canvas.drawLine(12,19,7,15,paint);
                canvas.drawArc(10,6,22,18,-70,140,false,paint);break;
            case "watch":
                canvas.drawOval(2,6,22,18,paint);canvas.drawCircle(12,12,3,paint);break;
            case "pip":
                canvas.drawRoundRect(2,4,22,20,2,2,paint);canvas.drawRect(12,12,19,17,paint);break;
            case "fullscreen":
                canvas.drawLine(3,9,3,3,paint);canvas.drawLine(3,3,9,3,paint);canvas.drawLine(15,3,21,3,paint);canvas.drawLine(21,3,21,9,paint);
                canvas.drawLine(3,15,3,21,paint);canvas.drawLine(3,21,9,21,paint);canvas.drawLine(15,21,21,21,paint);canvas.drawLine(21,21,21,15,paint);break;
            case "diagnostics":
                canvas.drawLine(3,20,21,20,paint);canvas.drawLine(6,16,6,12,paint);
                canvas.drawLine(12,16,12,8,paint);canvas.drawLine(18,16,18,5,paint);break;
            case "mouse":
                canvas.drawRoundRect(6,2,18,22,6,6,paint);canvas.drawLine(12,3,12,9,paint);break;
            default:
                for(int x=5;x<=19;x+=7)for(int y=5;y<=19;y+=7)canvas.drawCircle(x,y,1,paint);
        }
        if(warning){paint.setStyle(Paint.Style.FILL);int old=paint.getColor();paint.setColor(HubStreamUi.WARNING);
            canvas.drawCircle(20,4,3.2f,paint);paint.setColor(old);}
        canvas.restore();
    }
    @Override public void setAlpha(int alpha){paint.setAlpha(alpha);invalidateSelf();}
    @Override public void setColorFilter(ColorFilter filter){paint.setColorFilter(filter);invalidateSelf();}
    @Override public int getOpacity(){return PixelFormat.TRANSLUCENT;}
}
