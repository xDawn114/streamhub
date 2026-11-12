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
}
