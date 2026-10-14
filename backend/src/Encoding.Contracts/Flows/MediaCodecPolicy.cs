namespace Encoding.Contracts.Flows;

public static class MediaCodecPolicy
{
    public static string? VideoEncoder(string codec,string hardware,IReadOnlyList<string> available)
    {
        var candidates = VideoEncoderCatalog.All.Where(encoder => encoder.Codec == codec && encoder.Acceleration == hardware);
        return candidates.Select(encoder => encoder.Id).FirstOrDefault(available.Contains);
    }

    public static (VideoEncoderDescriptor Encoder, EncodingProfileTarget Target)? ResolveVideoEncoder(
        string codec,
        string mode,
        string? selectedEncoderId,
        EncodingProfileSnapshot profile,
        IReadOnlyList<string> availableEncoders,
        IReadOnlyList<string> availableAccelerations,
        IReadOnlyList<Encoding.Contracts.Nodes.NodeEncoderCapability>? encoderChecks = null)
    {
        var targets = profile.Targets
            .Where(target => VideoEncoderCatalog.Find(target.EncoderId)?.Codec == codec)
            .Where(target => mode != "specific" || target.EncoderId == selectedEncoderId)
            .OrderBy(target => target.Priority);
        foreach (var target in targets)
        {
            var descriptor = VideoEncoderCatalog.Find(target.EncoderId)!;
            var verified = encoderChecks is null
                || encoderChecks.Any(check => check.EncoderId == descriptor.Id && check.Status == "usable");
            if (availableEncoders.Contains(descriptor.Id, StringComparer.Ordinal)
                && availableAccelerations.Contains(descriptor.Acceleration, StringComparer.Ordinal)
                && verified)
                return (descriptor, target);
        }
        return null;
    }

    public static string? AudioEncoder(string codec,IReadOnlyList<string> available)=>
        (codec=="opus"?new[]{"libopus","opus"}:new[]{codec}).FirstOrDefault(available.Contains);
}
