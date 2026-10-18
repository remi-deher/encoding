using System.Text.Json;

namespace Encoding.Contracts.Flows;

public static class FlowMediaValues
{
    public static bool IsFfmpegPlan(string id) => id is "base-transcode-video" or "base-transcode-audio"
        or "base-normalize-audio" or "base-resize-video" or "base-remux-media"
        or "base-auto-crop" or "base-deinterlace-video";

    public static JsonElement Probe(string id, JsonElement metadata, int trackId = 0)
    {
        if (id == "base-probe-media") return metadata;
        var selected = metadata.GetProperty("streams").EnumerateArray().Where(stream => id == "base-probe-tracks"
            || stream.GetProperty("codec_type").GetString() == (id == "base-probe-audio" ? "audio" : "video"));
        if (id is "base-probe-video" or "base-probe-audio") selected = selected.Skip(trackId).Take(1);
        return JsonSerializer.SerializeToElement(selected.ToArray());
    }
}
