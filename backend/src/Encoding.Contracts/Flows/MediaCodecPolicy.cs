namespace Encoding.Contracts.Flows;

public static class MediaCodecPolicy
{
    public static string? VideoEncoder(string codec,string hardware,IReadOnlyList<string> available)
    {
        var choices=hardware switch
        {
            "nvenc"=>new[]{codec+"_nvenc"},"qsv"=>new[]{codec+"_qsv"},"vaapi"=>new[]{codec+"_vaapi"},
            "cpu"=>codec switch { "hevc"=>["libx265"],"h264"=>["libx264"],"av1"=>["libsvtav1","libaom-av1"],_=>Array.Empty<string>() },
            _=>Array.Empty<string>()
        };
        return choices.FirstOrDefault(available.Contains);
    }
    public static string? AudioEncoder(string codec,IReadOnlyList<string> available)=>
        (codec=="opus"?new[]{"libopus","opus"}:new[]{codec}).FirstOrDefault(available.Contains);
}
