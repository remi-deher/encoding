using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed partial class MediaActions
{
    public async Task<MediaActionOutcome> FfmpegPlanAsync(IReadOnlyList<RuntimePlanStep> plan,string current,string directory,string node,Guid workerId,
        CancellationToken ct,bool preserve=true,IReadOnlySet<int>? forceCpuFallbackSteps=null,JsonElement? knownMetadata=null)
    {
        // This input is immutable while the command is composed. Share one mandatory
        // inspection across selection, resize and video policy; output validation is separate.
        JsonElement? inputMetadata = knownMetadata;
        async Task<JsonElement> InspectAsync() => inputMetadata ??= await scanner.ScanAsync(current, ct);
        if (plan.Any(step => step.Configuration.ContainsKey("trackSelection")))
            plan = MediaTrackSelection.Expand(plan, await InspectAsync());
        if (plan.Any(step => step.ComponentId is "base-resize-video" or "video-resize"))
        {
            var metadata = await InspectAsync();
            var video = metadata.GetProperty("streams").EnumerateArray().FirstOrDefault(stream => MediaTrackSelection.Text(stream, "codec_type") == "video");
            var width = video.ValueKind == JsonValueKind.Object ? MediaTrackSelection.Number(video, "width") : 0;
            var height = video.ValueKind == JsonValueKind.Object ? MediaTrackSelection.Number(video, "height") : 0;
            plan = plan.Where(step => step.ComponentId is not ("base-resize-video" or "video-resize") || VideoResizePolicy.ShouldResize(step.Configuration, width, height)).ToArray();
        }
        if (plan.Count == 0) return new(null, []);
        var output=Output(directory,node,Path.GetExtension(current) is ".mp4" or ".mkv" or ".mka" or ".flac"?Path.GetExtension(current):".mkv");
        var input=new List<string> { "-nostdin","-y","-v","error" }; var args=new List<string> { "-c","copy" }; var videoFilters=new List<string>(); var hardwareMode="cpu";
        var resolutions=new List<ResolvedVideoEncoding>();
        var hardwareFallbackSteps=new HashSet<int>();
        if(preserve || !plan.Any(p=>p.ComponentId is "base-transcode-video" or "video-transcode" or "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize" or "base-resize-video" or "video-resize" or "base-auto-crop" or "video-auto-crop" or "base-deinterlace-video" or "video-deinterlace")) args.AddRange(["-map",
            "0"]);
        else {
            if(plan.Any(p=>p.ComponentId is "base-transcode-video" or "video-transcode" or "base-resize-video" or "video-resize" or "base-auto-crop" or "video-auto-crop" or "base-deinterlace-video" or "video-deinterlace")) args.AddRange(["-map",
                "0:v?"]);
            foreach(var track in plan.Where(p=>p.ComponentId is "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize").Select(p=>Number(p.Configuration,
                "trackId")).Distinct()) args.AddRange(["-map","0:a:"+track]);
        }
        for(var stepIndex=0;stepIndex<plan.Count;stepIndex++)
        {
            var step=plan[stepIndex];
            var c=step.Configuration;
            switch(step.ComponentId)
            {
                case "base-transcode-video" or "video-transcode":
                {
                    var codec=Text(c,"videoCodec","hevc");
                    var ffmpeg=(await capabilities.DiscoverAsync(ct)).FirstOrDefault(capability=>capability.ToolId=="ffmpeg")
                        ??throw new MediaToolException("FFmpeg",1);
                    JsonElement? currentMetadata=await InspectAsync();
                    JsonElement? vStream=null; int sourceWidth=0, sourceHeight=0; string? sourcePixFmt=null;
                    if(currentMetadata is { ValueKind: JsonValueKind.Object } metaObj && metaObj.TryGetProperty("streams",out var streamList) && streamList.ValueKind==JsonValueKind.Array)
                    {
                        foreach(var s in streamList.EnumerateArray())
                        {
                            if(s.TryGetProperty("codec_type",out var ctProp) && ctProp.GetString()=="video")
                            {
                                vStream=s;
                                if(s.TryGetProperty("width",out var w) && w.TryGetInt32(out var widthVal)) sourceWidth=widthVal;
                                if(s.TryGetProperty("height",out var h) && h.TryGetInt32(out var heightVal)) sourceHeight=heightVal;
                                if(s.TryGetProperty("pix_fmt",out var pf) && pf.ValueKind==JsonValueKind.String) sourcePixFmt=pf.GetString();
                                break;
                            }
                        }
                    }
                    var qualityTier=Text(c,"qualityTier","custom");
                    var colorDepth=Text(c,"colorDepth","auto");
                    var preserveHdr=Flag(c,"preserveHdr",true);
                    var retryWithCpu=Flag(c,"retryWithCpu");
                    if(c.TryGetValue("encodingProfile",out var profileValue) && profileValue.ValueKind==JsonValueKind.Object)
                    {
                        var profile=profileValue.Deserialize<EncodingProfileSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                            ??throw new MediaToolException("profil d’encodage",1);
                        var mode=Text(c,"encoderMode","automatic");
                        var selected=Text(c,"encoderId");
                        var forceCpu=forceCpuFallbackSteps?.Contains(stepIndex)==true;
                        var resolved=forceCpu
                            ?MediaCodecPolicy.ResolveCpuFallbackVideoEncoder(codec,profile,ffmpeg.Encoders??[],ffmpeg.AccelerationTypes,ffmpeg.EncoderChecks)
                            :MediaCodecPolicy.ResolveVideoEncoder(codec,mode,selected,profile,ffmpeg.Encoders??[],ffmpeg.AccelerationTypes,ffmpeg.EncoderChecks);
                        var selectedAcceleration=mode=="specific"?VideoEncoderCatalog.Find(selected)?.Acceleration:null;
                        var selectedUsesHardware=selectedAcceleration is not null&&selectedAcceleration!="cpu";
                        var profileHasHardwareTarget=profile.Targets.Any(target=>VideoEncoderCatalog.Find(target.EncoderId) is { } descriptor
                            && descriptor.Codec==codec && descriptor.Acceleration!="cpu");
                        if(resolved is null&&!forceCpu&&retryWithCpu&&(selectedUsesHardware||mode=="automatic"&&profileHasHardwareTarget))
                            resolved=MediaCodecPolicy.ResolveCpuFallbackVideoEncoder(codec,profile,ffmpeg.Encoders??[],ffmpeg.AccelerationTypes,ffmpeg.EncoderChecks);
                        if(resolved is null) throw new MediaToolException("encodeur vidéo compatible avec le profil",1);
                        var selectedTarget=resolved.Value;
                        var encoder=selectedTarget.Encoder;
                        var target=selectedTarget.Target;
                        var quality=MediaCodecPolicy.ResolveQuality(encoder,qualityTier,sourceWidth,sourceHeight,target.Quality);
                        hardwareMode=encoder.Acceleration;
                        if(hardwareMode!="cpu"&&retryWithCpu) hardwareFallbackSteps.Add(stepIndex);
                        if(hardwareMode=="vaapi") input.AddRange(["-vaapi_device",GpuDeviceSelector.RenderDevice()]);
                        args.AddRange(["-c:v",encoder.Id,encoder.QualityOption,quality.ToString(CultureInfo.InvariantCulture)]);
                        var preset=encoder.Presets.FirstOrDefault(candidate=>candidate.Id==target.Preset)?.Value;
                        if(!string.IsNullOrEmpty(encoder.PresetOption) && !string.IsNullOrEmpty(preset))
                            args.AddRange([encoder.PresetOption,preset]);
                        var pixFmt=MediaCodecPolicy.ResolvePixelFormat(encoder,colorDepth,sourcePixFmt);
                        if(!string.IsNullOrEmpty(pixFmt)) args.AddRange(["-pix_fmt",pixFmt]);
                        if(preserveHdr) MediaCodecPolicy.ApplyHdrMetadata(args,vStream,encoder.Id);
                        // Older durable plans predate FlowNodeId. Keep them executable, but do not
                        // record a misleading resolution under the enclosing processing node.
                        if(step.FlowNodeId is { } flowNodeId)
                            resolutions.Add(new(flowNodeId,workerId,codec,encoder.Id,ffmpeg.Version,encoder.Acceleration,
                                encoder.QualityMode,encoder.QualityOption,quality,target.Preset,
                                string.IsNullOrEmpty(encoder.PresetOption)?null:encoder.PresetOption,preset,
                                profile.Id,profile.Version,profile.Name,target.Priority,DateTimeOffset.UtcNow));
                    }
                    else
                    {
                        var forceCpu=forceCpuFallbackSteps?.Contains(stepIndex)==true;
                        var hardware=forceCpu?"cpu":Text(c,"hardwareAcceleration","cpu");
                        if(!forceCpu&&hardware!="cpu"&&retryWithCpu&&MediaCodecPolicy.VideoEncoder(codec,hardware,ffmpeg.Encoders??[]) is null) hardware="cpu";
                        hardwareMode=hardware;
                        var available=ffmpeg.Encoders??[];
                        var encoder=MediaCodecPolicy.VideoEncoder(codec,hardware,available)??throw new MediaToolException("encodeur vidéo",1);
                        if(hardware!="cpu"&&retryWithCpu) hardwareFallbackSteps.Add(stepIndex);
                        var encDesc=VideoEncoderCatalog.Find(encoder);
                        var fallbackQ=decimal.Parse(Number(c,"quality",23),CultureInfo.InvariantCulture);
                        var quality=encDesc is not null?MediaCodecPolicy.ResolveQuality(encDesc,qualityTier,sourceWidth,sourceHeight,fallbackQ):fallbackQ;
                        if(hardware=="vaapi") input.AddRange(["-vaapi_device",GpuDeviceSelector.RenderDevice()]);
                        args.AddRange(["-c:v",encoder,hardware=="cpu"?"-crf":hardware=="nvenc"?"-cq":hardware=="qsv"?"-global_quality":"-qp",quality.ToString(CultureInfo.InvariantCulture)]);
                        var preset=Text(c,"preset","medium");
                        if(encoder=="libsvtav1") preset=preset=="slow"?"4":preset=="fast"?"10":"8";
                        if(hardware=="nvenc") preset=preset=="slow"?"p7":preset=="fast"?"p1":"p4";
                        if(hardware!="vaapi") args.AddRange(["-preset",preset]);
                        if(encDesc is not null)
                        {
                            var pixFmt=MediaCodecPolicy.ResolvePixelFormat(encDesc,colorDepth,sourcePixFmt);
                            if(!string.IsNullOrEmpty(pixFmt)) args.AddRange(["-pix_fmt",pixFmt]);
                        }
                        if(preserveHdr) MediaCodecPolicy.ApplyHdrMetadata(args,vStream,encoder);
                    }
                    break;
                }
                case "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize": break;
                case "base-resize-video" or "video-resize": videoFilters.Add(VideoResizePolicy.Filter(c));
                    if(!plan.Any(p=>p.ComponentId is "base-transcode-video" or "video-transcode")) args.AddRange(["-c:v","libx264"]); break;
                case "base-auto-crop" or "video-auto-crop":
                    var cropF=Text(c,"cropFilter");
                    if(!string.IsNullOrEmpty(cropF)) videoFilters.Add(cropF);
                    if(!plan.Any(p=>p.ComponentId is "base-transcode-video" or "video-transcode")) args.AddRange(["-c:v","libx264"]);
                    break;
                case "base-deinterlace-video" or "video-deinterlace":
                    var deintAlgo=Text(c,"algorithm","bwdif");
                    if(!string.IsNullOrEmpty(deintAlgo)) videoFilters.Add(deintAlgo);
                    if(!plan.Any(p=>p.ComponentId is "base-transcode-video" or "video-transcode")) args.AddRange(["-c:v","libx264"]);
                    break;
                case "base-remux-media" or "video-remux-media": output=Output(directory,node,"."+Text(c,"container","mkv")); break;
                default: throw new IOException("Ce réglage ne fait pas partie d’un plan FFmpeg.");
            }
        }
        if (await AppendAudioPlanAsync(plan, current, directory, input, args, preserve, ct) is { } audioExtension
            && Path.GetExtension(output) == Path.GetExtension(current))
            output = Output(directory, node, audioExtension);
        if(hardwareMode=="vaapi") videoFilters.AddRange(["format=nv12","hwupload"]);
        if(videoFilters.Count>0) args.AddRange(["-vf",string.Join(',',videoFilters)]);
        input.AddRange(["-i",current]); input.AddRange(args); input.Add(output);
        try
        {
            await tools.CheckedAsync("ffmpeg",input,directory,ct);
        }
        catch(MediaToolException) when(hardwareFallbackSteps.Count>0&&forceCpuFallbackSteps is null)
        {
            ct.ThrowIfCancellationRequested();
            if(File.Exists(output)) File.Delete(output);
            logger?.LogWarning("Échec d’un encodage matériel dans {Node} ; nouvel essai avec les encodeurs CPU compatibles.",node);
            return await FfmpegPlanAsync(plan,current,directory,node,workerId,ct,preserve,hardwareFallbackSteps,inputMetadata);
        }
        return new(output,[],null,resolutions);
    }

    private async Task<MediaActionOutcome> MatroskaAsync(string id,IReadOnlyDictionary<string,JsonElement> c,string current,string directory,string node,
        Func<string,Task<string>> extra,CancellationToken ct)
    {
        var file=Output(directory,node,".mkv");
        if(id is "base-extract-track" or "base-extract-attachments" or "base-extract-chapters" or "mkv-extract-track" or "mkv-extract-attachments" or "mkv-extract-chapters")
        {
            if(id is "base-extract-chapters" or "mkv-extract-chapters")
            {
                var simple=Text(c,"format","xml")!="xml"; var args=new List<string> { current,"chapters" }; if(simple) args.Add("--simple");
                file=Output(directory,node,simple?".txt":".xml"); var text=await tools.CheckedAsync("mkvextract",args,directory,ct);
                    await File.WriteAllTextAsync(file,text,ct); return new(null,[file]);
            }
            var tracks=(id is "base-extract-track" or "mkv-extract-track")?[int.Parse(Number(c,"trackId"))]:Ids(Text(c,"attachmentIds"));
            if(tracks.Length==0)
            {
                var identification=await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct); using var json=JsonDocument.Parse(identification);
                tracks=json.RootElement.GetProperty("attachments").EnumerateArray().Select(a=>a.GetProperty("id").GetInt32()).ToArray();
            }
            var args2=new List<string> { current,(id is "base-extract-track" or "mkv-extract-track")?"tracks":"attachments" }; var files=new List<string>();
            foreach(var track in tracks) { var result=Output(directory,node+track,".bin"); files.Add(result); args2.Add(track+":"+result); }
            await tools.CheckedAsync("mkvextract",args2,directory,ct); return new(null,files);
        }
        if(id is "base-edit-track-properties" or "base-edit-matroska-metadata" or "base-remove-attachments" or "base-replace-chapters" or "mkv-edit-track-properties" or "mkv-edit-matroska-metadata" or "mkv-remove-attachments" or "mkv-replace-chapters")
        {
            File.Copy(current,file,true); var args=new List<string> { file };
            if(id is "base-edit-matroska-metadata" or "mkv-edit-matroska-metadata") args.AddRange(["--edit","info","--set","title="+Text(c,"title")]);
            if(id is "base-edit-track-properties" or "mkv-edit-track-properties")
            {
                var raw=await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct); using var identification=JsonDocument.Parse(raw);
                var track=identification.RootElement.GetProperty("tracks").EnumerateArray().Single(t=>t.GetProperty("id").GetInt32()==int.Parse(Number(c,"trackId")));
                args.AddRange(["--edit","track:="+track.GetProperty("properties").GetProperty("uid").GetUInt64()]);
                foreach(var pair in new[]{("language","language"),("trackName","name")}) if(Text(c,pair.Item1)!="") args.AddRange(["--set",pair.Item2+"="+Text(c,pair.Item1)]);
                args.AddRange(["--set","flag-default="+(Flag(c,"defaultTrack")?"1":"0"),"--set","flag-forced="+(Flag(c,"forcedTrack")?"1":"0")]);
            }
            if(id is "base-replace-chapters" or "mkv-replace-chapters") args.AddRange(["--chapters",await extra(Text(c,"sourceFile"))]);
            if(id is "base-remove-attachments" or "mkv-remove-attachments")
            {
                var selected=Ids(Text(c,"attachmentIds"));
                if(selected.Length==0) { await tools.CheckedAsync("mkvmerge",["-o",file+".next.mkv","--no-attachments",current],directory,ct);
                    File.Move(file+".next.mkv",file,true); return new(file,[]); }
                foreach(var attachment in selected) args.AddRange(["--delete-attachment",attachment.ToString()]);
            }
            await tools.CheckedAsync("mkvpropedit",args,directory,ct); return new(file,[]);
        }
        var merge=new List<string> { "-o",file };
        if(id is "base-reorder-tracks" or "mkv-reorder-tracks") merge.AddRange(["--track-order",string.Join(',',Ids(Text(c,"trackOrder")).Select(track=>"0:"+track))]);
        if(id is "base-remux-matroska" or "base-remove-tracks" or "mkv-remux-matroska" or "mkv-remove-tracks")
        {
            var types=Text(c,"trackType","all")=="all"?new[]{"video","audio","subtitles"}:new[]{Text(c,"trackType")};
            var ids=Ids(Text(c,"trackIds"));
            if(Text(c,"language")!="") {
                var raw=await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct); using var identified=JsonDocument.Parse(raw);
                ids=identified.RootElement.GetProperty("tracks").EnumerateArray().Where(track=>types.Contains(track.GetProperty("type").GetString())
                    && track.GetProperty("properties").TryGetProperty("language",out var language)&&language.GetString()==Text(c,"language")
                    && (ids.Length==0||ids.Contains(track.GetProperty("id").GetInt32()))).Select(track=>track.GetProperty("id").GetInt32()).ToArray();
                if(ids.Length==0) throw new MediaToolException("sélection de pistes",1);
            }
            if(ids.Length>0) foreach(var type in types) merge.AddRange(["--"+(type=="subtitles"?"subtitle":type)+"-tracks",
                ((id is "base-remove-tracks" or "mkv-remove-tracks")?"!":"")+string.Join(',',ids)]);
            if((id is "base-remove-tracks" or "mkv-remove-tracks") && ids.Length==0) foreach(var type in types) merge.Add("--no-"+type);
            if(Text(c,"title")!="") merge.AddRange(["--title",Text(c,"title")]);
            if((id is "base-remux-matroska" or "mkv-remux-matroska") && !Flag(c,"keepAttachments",true)) merge.Add("--no-attachments");
        }
        if(id is not("base-remux-matroska" or "base-remove-tracks" or "base-reorder-tracks" or "base-add-audio-track" or "base-add-subtitle-track" or "mkv-remux-matroska" or "mkv-remove-tracks" or "mkv-reorder-tracks" or "audio-add-track" or "subtitles-add-track")) throw new IOException("Action média inconnue.");
        merge.Add(current);
        if(id is "base-add-audio-track" or "audio-add-track" or "base-add-subtitle-track" or "subtitles-add-track")
        {
            if(Text(c,"language")!="") merge.AddRange(["--language","0:"+Text(c,"language")]);
            if(Text(c,"trackName")!="") merge.AddRange(["--track-name","0:"+Text(c,"trackName")]);
            merge.AddRange([id is "base-add-audio-track" or "audio-add-track"?"--default-track-flag":"--forced-display-flag","0:"+(Flag(c,
                id is "base-add-audio-track" or "audio-add-track"?"defaultTrack":"forcedTrack")?"1":"0")]);
            merge.Add(await extra(Text(c,"sourceFile")));
        }
        await tools.CheckedAsync("mkvmerge",merge,directory,ct); return new(file,[]);
    }
}
