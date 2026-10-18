using System.Text.Json;

namespace Encoding.Contracts.Flows;

public static class FlowMediaValues
{
    public static bool IsFfmpegPlan(string id) => id is "base-transcode-video" or "base-transcode-audio"
        or "base-normalize-audio" or "base-resize-video" or "base-remux-media"
        or "base-auto-crop" or "base-deinterlace-video";

    public static JsonElement Probe(string id, JsonElement metadata, int trackId = 0)
    {
        if (id is "base-probe-media" or "probe-media") return metadata;
        var selected = metadata.GetProperty("streams").EnumerateArray().Where(stream => id is "base-probe-tracks" or "probe-tracks"
            || stream.GetProperty("codec_type").GetString() == (id is "base-probe-audio" or "probe-audio" ? "audio" : "video"));
        if (id is "base-probe-video" or "probe-video" or "base-probe-audio" or "probe-audio") selected = selected.Skip(trackId).Take(1);
        return JsonSerializer.SerializeToElement(selected.ToArray());
    }
}
