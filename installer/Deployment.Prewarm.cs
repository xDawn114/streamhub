using StreamHub.Host.Runtime;

namespace StreamHub.Installer;

internal sealed partial class Deployment
{
    private void WaitForEncoderPrewarm()
    {
        // Installation is already committed. A missing desktop, paused host or
        // probe failure must not turn a successful upgrade into a rollback.
        try
        {
            Log("正在准备首次连接……");
            Stage("encoder-prewarm-wait");
            var client = new RuntimeClient();
            var outcome = EncoderPrewarm.WaitAsync(client.GetStatusAsync, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Stage("encoder-prewarm-" + outcome);
            Log(outcome == "ready" ? "首次连接准备完成。" : "首次连接准备已延后；连接时会自动检查。");
        }
        catch (Exception)
        {
            try { Stage("encoder-prewarm-deferred-error"); } catch { }
            Log("首次连接准备已延后；连接时会自动检查。");
        }
    }
}
