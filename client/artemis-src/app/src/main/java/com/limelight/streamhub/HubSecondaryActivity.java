package com.limelight.streamhub;

import android.app.Activity;
import android.os.Bundle;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.view.Gravity;
import android.view.View;
import android.widget.*;
import com.limelight.R;

/** Shared fixed header, readable system bars and bounded content for secondary pages. */
abstract class HubSecondaryActivity extends Activity {
    protected LinearLayout content;
    protected TextView feedback;
    @Override public void onCreate(Bundle state) {
        setTheme(HubTheme.settingsTheme(this)); super.onCreate(state); HubNavigation.prepareWindow(this);
        getWindow().setStatusBarColor(HubTheme.background(this));getWindow().setNavigationBarColor(HubTheme.background(this));
        getWindow().getDecorView().setSystemUiVisibility(HubTheme.isDark(this)?0:
                View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR);
        HubWindowLayout layout=HubWindowLayout.of(this);
        LinearLayout root=column();root.setBackgroundColor(HubTheme.background(this));
        int width=dp(layout.contentWidthDp());
        LinearLayout header=new LinearLayout(this);header.setGravity(Gravity.CENTER_VERTICAL);header.setPadding(dp(layout.pageInsetDp()),dp(layout.compact()?16:24),dp(layout.pageInsetDp()),dp(8));
        ImageButton back=new ImageButton(this);back.setImageResource(R.drawable.ic_hub_back);
        back.setImageTintList(android.content.res.ColorStateList.valueOf(HubTheme.text(this)));
        back.setPadding(dp(12),dp(12),dp(12),dp(12));back.setBackgroundColor(android.graphics.Color.TRANSPARENT);back.setContentDescription("返回上一级");back.setOnClickListener(v->finish());
        header.addView(back,new LinearLayout.LayoutParams(dp(48),dp(48)));
        TextView title=text(pageTitle(),24,false);title.setTypeface(null,Typeface.BOLD);
        title.setGravity(Gravity.CENTER_VERTICAL);title.setIncludeFontPadding(false);
        header.addView(title,new LinearLayout.LayoutParams(0,-2,1));
        FrameLayout headerFrame=new FrameLayout(this);headerFrame.addView(header,new FrameLayout.LayoutParams(width,-2,Gravity.TOP|Gravity.CENTER_HORIZONTAL));root.addView(headerFrame);
        ScrollView scroll=new ScrollView(this);scroll.setFillViewport(true);scroll.setClipToPadding(true);
        FrameLayout centered=new FrameLayout(this);content=column();content.setPadding(dp(layout.pageInsetDp()),0,dp(layout.pageInsetDp()),dp(32));
        centered.addView(content,new FrameLayout.LayoutParams(width,-2,Gravity.TOP|Gravity.CENTER_HORIZONTAL));
        scroll.addView(centered);root.addView(scroll,new LinearLayout.LayoutParams(-1,0,1));
        HubNavigation navigation=new HubNavigation(this,root,2,page->{
            if(page==3)HubFilesActivity.open(this);
            else startActivity(new android.content.Intent(this,HubActivity.class).addFlags(android.content.Intent.FLAG_ACTIVITY_CLEAR_TOP|android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP).putExtra("HubPage",page),HubNavigation.transition(this));
            finish();
        });setContentView(navigation.view);
        feedback=text("",14,false);feedback.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);
        content.addView(feedback);message("");
    }
    protected abstract String pageTitle();
    protected int dp(int value){return Math.round(value*getResources().getDisplayMetrics().density);}
    protected LinearLayout column(){LinearLayout value=new LinearLayout(this);value.setOrientation(LinearLayout.VERTICAL);return value;}
    protected TextView text(String value,int size,boolean muted){TextView view=new TextView(this);view.setText(value);view.setTextSize(size);
        view.setTextColor(muted?HubTheme.muted(this):HubTheme.text(this));view.setLineSpacing(dp(3),1);return view;}
    protected LinearLayout card(LinearLayout parent,String title){
        LinearLayout box=column();box.setPadding(dp(16),dp(16),dp(16),dp(16));GradientDrawable bg=new GradientDrawable();
        bg.setColor(HubTheme.surface(this));bg.setCornerRadius(dp(16));box.setBackground(bg);box.setElevation(0);
        LinearLayout.LayoutParams size=new LinearLayout.LayoutParams(-1,-2);size.topMargin=dp(16);parent.addView(box,size);
        if(!title.isEmpty()){TextView label=text(title,17,false);label.setTypeface(null,Typeface.BOLD);box.addView(label);}
        return box;
    }
    protected Button action(LinearLayout parent,String label,boolean primary,Runnable run){
        Button button=new Button(this);button.setText(label);HubButtons.apply(button,primary);button.setOnClickListener(v->run.run());
        LinearLayout.LayoutParams size=new LinearLayout.LayoutParams(-1,-2);size.topMargin=dp(8);parent.addView(button,size);return button;
    }
    protected void message(String value){feedback.setText(value);feedback.setVisibility(value.isEmpty()?View.GONE:View.VISIBLE);}
}
