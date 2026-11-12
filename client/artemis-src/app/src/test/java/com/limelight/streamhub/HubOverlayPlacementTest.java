package com.limelight.streamhub;

import org.junit.Test;
import static org.junit.Assert.*;

public class HubOverlayPlacementTest {
    private void contained(HubOverlayPlacement.Box child,HubOverlayPlacement.Box safe) {
        assertTrue(child.x>=safe.x && child.y>=safe.y);
        assertTrue(child.x+child.width<=safe.x+safe.width);
        assertTrue(child.y+child.height<=safe.y+safe.height);
    }
    @Test public void allEdgesRemainReachableAndMenusStayInWindow() {
        for(int[] size:new int[][]{{400,900},{1300,650},{360,240}}) {
            HubOverlayPlacement.Box safe=new HubOverlayPlacement.Box(24,32,size[0],size[1]);
            for(int x:new int[]{safe.x,safe.x+safe.width/2,safe.x+safe.width-88})
                for(int y:new int[]{safe.y,safe.y+safe.height/2,safe.y+safe.height-48}) {
                    HubOverlayPlacement.Box anchor=HubOverlayPlacement.anchor(x,y,88,48,safe);
                    assertEquals(x,anchor.x);assertEquals(y,anchor.y);
                    contained(anchor,safe);contained(HubOverlayPlacement.menu(anchor,420,620,safe,8,true),safe);
                }
        }
    }
    @Test public void rightEdgeOpensToLeftWithoutMovingTheButton() {
        HubOverlayPlacement.Box safe=new HubOverlayPlacement.Box(8,8,1200,900);
        HubOverlayPlacement.Box anchor=HubOverlayPlacement.anchor(1120,160,88,48,safe);
        HubOverlayPlacement.Box menu=HubOverlayPlacement.menu(anchor,420,620,safe,8,true);
        assertEquals(1120,anchor.x);assertTrue(menu.x+menu.width<=anchor.x-8);
    }
    @Test public void keyboardAndRotationClampAnchorByItsOwnSize() {
        HubOverlayPlacement.Box keyboard=new HubOverlayPlacement.Box(12,24,370,300);
        HubOverlayPlacement.Box anchor=HubOverlayPlacement.anchor(1200,900,88,48,keyboard);
        assertEquals(294,anchor.x);assertEquals(276,anchor.y);
        contained(HubOverlayPlacement.menu(anchor,420,620,keyboard,8,true),keyboard);
    }
}
