namespace StreamHub.Host.Runtime;

/// <summary>A bounded receiver hint. It never authorizes changes to an active engine.</summary>
public static class EncoderProbePlan
{
    public static string Validate(string? plan) => plan switch
    {
        null or "" or "full" => "full",
        "sdr-auto" or "sdr-h264" or "sdr-hevc" or "sdr-av1" => plan,
        _ => throw new ArgumentException("编码探测参数无效，请更新客户端。")
    };
    public static bool Covers(string current, string requested) =>
        current == "full" || current == requested || current == "sdr-auto" && requested.StartsWith("sdr-", StringComparison.Ordinal);
}
