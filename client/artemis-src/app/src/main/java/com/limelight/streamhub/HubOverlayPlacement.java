package com.limelight.streamhub;

/** Anchor and menu have independent bounds. All coordinates are window relative. */
public final class HubOverlayPlacement {
    private HubOverlayPlacement() { }
    public static final class Box {
        public final int x, y, width, height;
        public Box(int x, int y, int width, int height) {
            this.x=x; this.y=y; this.width=Math.max(1,width); this.height=Math.max(1,height);
        }
    }
    private static int clamp(int value,int low,int high) { return Math.max(low,Math.min(value,Math.max(low,high))); }
    public static Box anchor(int x,int y,int width,int height,Box safe) {
        width=Math.min(width,safe.width); height=Math.min(height,safe.height);
        return new Box(clamp(x,safe.x,safe.x+safe.width-width),clamp(y,safe.y,safe.y+safe.height-height),width,height);
    }
    public static Box menu(Box anchor,int width,int height,Box safe,int gap,boolean adaptive) {
        width=Math.min(width,safe.width); height=Math.min(height,safe.height);
        if(!adaptive)return anchor(safe.x+(safe.width-width)/2,safe.y,width,height,safe);
        int[][] positions={{anchor.x+anchor.width+gap,anchor.y},{anchor.x-width-gap,anchor.y},
                {anchor.x,anchor.y+anchor.height+gap},{anchor.x,anchor.y-height-gap}};
        long best=Long.MIN_VALUE; Box chosen=null;
        for(int[] point:positions) {
            Box candidate=anchor(point[0],point[1],width,height,safe);
            int overlapX=Math.max(0,Math.min(candidate.x+width,anchor.x+anchor.width)-Math.max(candidate.x,anchor.x));
            int overlapY=Math.max(0,Math.min(candidate.y+height,anchor.y+anchor.height)-Math.max(candidate.y,anchor.y));
            long score=-(long)overlapX*overlapY*1000-Math.abs(candidate.x-point[0])-Math.abs(candidate.y-point[1]);
            if(score>best){best=score;chosen=candidate;}
        }
        return chosen;
    }

    /** Prefer a usable lower letterbox; otherwise dock to the lower window edge. */
    public static Box bottomSheet(int width,int height,Box safe,Box video,int minimumHeight) {
        width=Math.min(width,safe.width);height=Math.min(height,safe.height);
        int top=safe.y;
        if(video!=null) {
            int videoBottom=Math.max(safe.y,video.y+video.height);
            int lowerGap=safe.y+safe.height-videoBottom;
            if(lowerGap>=minimumHeight){top=videoBottom;height=Math.min(height,lowerGap);}
        }
        return anchor(safe.x+(safe.width-width)/2,safe.y+safe.height-height,width,height,
                new Box(safe.x,top,safe.width,safe.y+safe.height-top));
    }

    public static Box sidePanel(Box anchor,int width,int height,Box safe,int gap) {
        if(safe.width<safe.height)return bottomSheet(width,height,safe,null,1);
        width=Math.min(width,safe.width);height=Math.min(height,safe.height);
        int right=safe.x+safe.width-(anchor.x+anchor.width+gap),left=anchor.x-gap-safe.x;
        // Expand away from the nearest edge, preserving a whole panel when possible.
        int x=right>=width?anchor.x+anchor.width+gap:left>=width?anchor.x-width-gap:
                right>=left?safe.x+safe.width-width:safe.x;
        return anchor(x,anchor.y,width,height,safe);
    }

    public static Box blackBars(int width,int height,Box safe,Box video,int minimumHeight) {
        // A real letterbox is useful only when it also leaves room for the scrolling body.
        minimumHeight=Math.min(safe.height,Math.max(minimumHeight,Math.round(safe.height*.45f)));
        height=Math.min(safe.height,Math.max(height,minimumHeight));
        if(video!=null){
            int upper=Math.max(0,Math.min(safe.height,video.y-safe.y));
            int lower=Math.max(0,Math.min(safe.height,safe.y+safe.height-video.y-video.height));
            if(Math.max(upper,lower)>=minimumHeight){
                Box bar=upper>lower?new Box(safe.x,safe.y,safe.width,upper):
                        new Box(safe.x,safe.y+safe.height-lower,safe.width,lower);
                // Use the bar's full width, leaving its opposite bar available for remote input.
                return anchor(bar.x,bar.y,bar.width,Math.min(height,bar.height),bar);
            }
        }
        return bottomSheet(safe.width<safe.height?safe.width:width,height,safe,null,minimumHeight);
    }
}
