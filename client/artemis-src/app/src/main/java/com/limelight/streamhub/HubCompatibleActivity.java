package com.limelight.streamhub;

import android.app.AlertDialog;
import android.content.*;
import android.os.*;
import android.net.Uri;
import android.widget.*;
import com.limelight.Game;
import com.limelight.LimeLog;
import com.limelight.binding.PlatformBinding;
import com.limelight.computers.ComputerManagerService;
import com.limelight.nvstream.http.*;
import com.limelight.utils.ServerHelper;
import java.util.*;
import java.util.concurrent.*;

/** The compatibility database never writes HubStore's bound device or account target. */
public final class HubCompatibleActivity extends HubSecondaryActivity {
    private final ExecutorService worker=Executors.newSingleThreadExecutor();
    private final Map<String,ComputerDetails> records=new LinkedHashMap<>();
    private final Set<String> removed=new HashSet<>();
    private final Handler main=new Handler(Looper.getMainLooper());
    private ComputerManagerService.ComputerManagerBinder manager;
    private LinearLayout list,apps;
    private Button add,search;
    private AlertDialog pinDialog;
    private boolean bound,foreground,busy,discoveryFinished;
    private final Runnable discoveryTimeout=()->{discoveryFinished=true;if(foreground)render();};
    private void searchAgain(){
        discoveryFinished=false;main.removeCallbacks(discoveryTimeout);main.postDelayed(discoveryTimeout,10_000);poll();render();
    }
    private long generation;
    private final ServiceConnection connection=new ServiceConnection(){
        @Override public void onServiceConnected(ComponentName name,IBinder service){
            manager=(ComputerManagerService.ComputerManagerBinder)service;
            final ComputerManagerService.ComputerManagerBinder active=manager;
            worker.execute(()->{active.waitForReady();main.post(()->{if(foreground&&manager==active){poll();render();}});});
        }
        @Override public void onServiceDisconnected(ComponentName name){manager=null;discoveryFinished=true;message("兼容主机服务已断开，请返回后重试。");render();}
    };
    @Override protected String pageTitle(){return "兼容主机";}
    @Override public void onCreate(Bundle state){
        super.onCreate(state);
        content.addView(text("连接支持兼容串流协议的电脑。此处的主机记录和配对独立保存，不会替换“我控制”已绑定的电脑。",14,true));
        add=action(content,"添加兼容主机",false,this::addAddress);
        search=action(content,"重新查找",false,this::searchAgain);
        list=column();content.addView(list);apps=column();content.addView(apps);render();
    }
    @Override protected void onStart(){super.onStart();foreground=true;
        bound=bindService(new Intent(this,ComputerManagerService.class),connection,Context.BIND_AUTO_CREATE);
        if(!bound){discoveryFinished=true;message("兼容主机服务无法启动，请重试。");render();}
        else searchAgain();}
    @Override protected void onStop(){foreground=false;generation++;main.removeCallbacks(discoveryTimeout);
        if(pinDialog!=null)pinDialog.dismiss();
        if(manager!=null)manager.stopPolling();if(bound){unbindService(connection);bound=false;}manager=null;
        super.onStop();}
    @Override protected void onDestroy(){worker.shutdownNow();main.removeCallbacksAndMessages(null);super.onDestroy();}
    private void poll(){
        if(manager==null||!foreground||busy)return;
        final long epoch=generation;final ComputerManagerService.ComputerManagerBinder active=manager;
        manager.startPolling(details->{ComputerDetails copy=new ComputerDetails(details);
            main.post(()->{if(foreground&&epoch==generation&&manager==active&&!removed.contains(copy.uuid)){records.put(copy.uuid,copy);render();}});});
    }
    private void render(){
        if(list==null)return;list.removeAllViews();add.setEnabled(manager!=null&&!busy);search.setEnabled(manager!=null&&!busy&&discoveryFinished);
        for(boolean online:new boolean[]{true,false}){
            LinearLayout group=null;
            for(ComputerDetails computer:records.values()){
                boolean live=computer.state==ComputerDetails.State.ONLINE && computer.activeAddress!=null;
                if(live!=online)continue;
                if(group==null)group=card(list,online?"在线主机":"离线主机");
                LinearLayout row=card(group,computer.name==null?"未命名电脑":computer.name);
                row.addView(text((live?"● 在线":"○ 离线")+" · "+address(computer),14,!live));
                boolean paired=computer.pairState==PairingManager.PairState.PAIRED;
                Button connect=action(row,paired?"查看应用与桌面":"配对此主机",false,()->{if(paired)loadApps(computer);else pair(computer);});
                connect.setEnabled(live&&!busy);connect.setContentDescription((live?"":"离线，无法连接；")+connect.getText());
                Button remove=action(row,"移除此记录",false,()->confirmRemove(computer));remove.setTextColor(HubTheme.danger(this));remove.setEnabled(!busy);
            }
        }
        if(records.isEmpty()){
            if(!discoveryFinished){
                ProgressBar progress=new ProgressBar(this);progress.setContentDescription("正在查找兼容主机");
                LinearLayout.LayoutParams size=new LinearLayout.LayoutParams(dp(32),dp(32));size.gravity=android.view.Gravity.CENTER_HORIZONTAL;size.topMargin=dp(24);list.addView(progress,size);
            }
            list.addView(text(discoveryFinished?manager==null?"兼容主机服务暂不可用，请返回后重试。":"未找到附近主机。请确认电脑已开启兼容串流服务，或手动添加电脑地址。":manager==null?"正在连接兼容主机服务…":"正在查找附近主机，约10秒；也可手动添加地址。",14,true));
        }
    }
    private String address(ComputerDetails computer){return computer.activeAddress!=null?computer.activeAddress.toString():
            computer.manualAddress!=null?computer.manualAddress.toString():computer.localAddress!=null?computer.localAddress.toString():computer.uuid;}
    private void addAddress(){
        EditText address=new EditText(this);address.setSingleLine();address.setTextColor(HubTheme.text(this));address.setHintTextColor(HubTheme.muted(this));
        address.setHint("电脑地址，可附端口");address.setPadding(dp(24),dp(12),dp(24),dp(12));
        AlertDialog dialog=HubStreamUi.hubDialog(this).setTitle("添加兼容主机").setView(address).setNegativeButton("取消",null).setPositiveButton("添加",null).create();
        dialog.setOnShowListener(d->dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v->{
            String raw=address.getText().toString().trim();
            Uri uri=Uri.parse("art://"+raw);
            if(uri.getHost()==null)uri=Uri.parse("art://["+raw+"]");
            int port=uri.getPort()==-1?NvHTTP.DEFAULT_HTTP_PORT:uri.getPort();
            if(raw.isEmpty()||raw.contains("/")||raw.contains("?")||raw.contains("#")||raw.contains("@")||uri.getHost()==null||port<1||port>65535){address.setError("请输入主机名或IP地址及有效端口");return;}
            ComputerDetails computer=new ComputerDetails();computer.manualAddress=new ComputerDetails.AddressTuple(uri.getHost(),port);
            dialog.dismiss();run("正在查找兼容主机…",active->{
                if(!active.addComputerBlocking(computer))throw new java.io.IOException("主机未响应，请确认地址、网络和串流服务。");
                main.post(()->{removed.clear();poll();});return "已添加兼容主机，可在列表中配对。";
            });
        }));dialog.show();
    }
    private void confirmRemove(ComputerDetails computer){
        HubStreamUi.hubDialog(this).setTitle("移除兼容主机记录？").setMessage(computer.name+"\n仅移除此平板保存的兼容主机记录；不会清除“我控制”绑定，也不会结束电脑上的应用。")
            .setNegativeButton("取消",null).setPositiveButton("移除",(d,w)->run("正在移除…",active->{
                active.removeComputer(computer);main.post(()->{removed.add(computer.uuid);records.remove(computer.uuid);apps.removeAllViews();});return "已移除兼容主机记录。";
            })).show().getButton(AlertDialog.BUTTON_POSITIVE).setTextColor(HubTheme.danger(this));
    }
    private NvHTTP http(ComputerManagerService.ComputerManagerBinder active,ComputerDetails computer)throws Exception{
        return new NvHTTP(ServerHelper.getCurrentAddressFromComputer(computer),computer.httpsPort,active.getUniqueId(),computer.serverCert,PlatformBinding.getCryptoProvider(this));
    }
    private void pair(ComputerDetails computer){
        String pin=PairingManager.generatePinString();
        pinDialog=HubStreamUi.hubDialog(this).setTitle("配对 "+computer.name).setMessage("请在这台电脑的串流服务中确认以下配对码：\n\n"+pin)
                .setNegativeButton("取消",(d,w)->{generation++;message("已取消等待配对结果。");}).create();
        pinDialog.show();long token=generation;
        run("正在等待电脑确认配对…",active->{
            active.stopPolling();active.waitForPollingStopped();
            if(token!=generation||!foreground)throw new CancellationException();
            NvHTTP http=http(active,computer);
            if(http.getPairState()!=PairingManager.PairState.PAIRED){
                PairingManager pairing=http.getPairingManager();
                PairingManager.PairState result=pairing.pair(http.getServerInfo(true),pin,null);
                if(result!=PairingManager.PairState.PAIRED)throw new java.io.IOException(result==PairingManager.PairState.PIN_WRONG?"配对码不正确，请重试。":"配对未完成："+result);
                ComputerDetails saved=active.getComputer(computer.uuid);
                if(saved==null)throw new java.io.IOException("主机记录已移除，请重新添加。");
                // Retain a successful certificate even if the Activity stopped while the host confirmed.
                saved.serverCert=pairing.getPairedCert();active.invalidateStateForComputer(computer.uuid);
            }
            main.post(()->{if(pinDialog!=null)pinDialog.dismiss();});return "配对成功，可查看应用与桌面。";
        });
    }
    private void loadApps(ComputerDetails computer){
        run("正在获取应用与桌面…",active->{
            List<NvApp> applications=http(active,computer).getAppList();
            main.post(()->{if(!foreground)return;apps.removeAllViews();LinearLayout box=card(apps,computer.name+" · 应用与桌面");
                if(applications.isEmpty())box.addView(text("电脑未发布应用，请在电脑端串流服务中添加桌面或应用。",14,true));
                for(NvApp app:applications)action(box,app.getAppName(),false,()->launch(computer.uuid,app));
                if(computer.runningGameId!=0){Button quit=action(box,"结束电脑上正在串流的应用",false,()->confirmQuit(computer));quit.setTextColor(HubTheme.danger(this));}
                box.requestFocus();box.requestRectangleOnScreen(new android.graphics.Rect(0,0,box.getWidth(),box.getHeight()),true);
            });return "已获取 "+applications.size()+" 项，点选后开始连接。";
        });
    }
    private void launch(String uuid,NvApp app){
        ComputerDetails current=manager==null?null:manager.getComputer(uuid);
        if(current==null||current.state!=ComputerDetails.State.ONLINE||current.activeAddress==null||current.pairState!=PairingManager.PairState.PAIRED){message("主机状态已变化，请刷新后重试。");return;}
        Intent intent=ServerHelper.createStartIntent(this,app,current,manager,false);intent.putExtra("HubCompatibleUi",true);startActivity(intent);
    }
    private void confirmQuit(ComputerDetails computer){
        HubStreamUi.hubDialog(this).setTitle("结束电脑上的串流应用？").setMessage("尚未保存的内容可能丢失。")
            .setNegativeButton("取消",null).setPositiveButton("结束应用",(d,w)->run("正在结束应用…",active->{
                if(!http(active,computer).quitApp())throw new java.io.IOException("电脑未允许结束应用。");active.invalidateStateForComputer(computer.uuid);return "已结束应用。";
            })).show().getButton(AlertDialog.BUTTON_POSITIVE).setTextColor(HubTheme.danger(this));
    }
    private interface Work{String execute(ComputerManagerService.ComputerManagerBinder active)throws Exception;}
    private void run(String label,Work work){
        if(busy||manager==null){message("请等待当前操作完成。");return;}
        busy=true;message(label);render();final ComputerManagerService.ComputerManagerBinder active=manager;final long token=generation;
        worker.execute(()->{
            String result;
            try{result=work.execute(active);}catch(CancellationException error){result="已取消。";}
            catch(Exception error){LimeLog.warning("Compatible host: "+error);result="操作未完成："+error.getMessage();}
            final String text=result;main.post(()->{busy=false;if(!foreground)return;
                if(token==generation){message(text);Toast.makeText(this,text,Toast.LENGTH_LONG).show();}if(pinDialog!=null)pinDialog.dismiss();poll();render();});
        });
    }
}
