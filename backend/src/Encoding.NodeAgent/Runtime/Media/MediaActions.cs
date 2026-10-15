using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed record MediaActionOutcome(string? Primary,IReadOnlyList<string> Additional,JsonElement? Value=null,
    IReadOnlyList<ResolvedVideoEncoding>? VideoEncodings=null);

public sealed class MediaActions(ProcessToolRunner tools,MediaScanner scanner,IToolCapabilityProbe capabilities)
{
    public static string Text(IReadOnlyDictionary<string,JsonElement> c,string key,string fallback="")=>c.TryGetValue(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:fallback;
    public static string Number(IReadOnlyDictionary<string,JsonElement> c,string key,decimal fallback=0)=>c.TryGetValue(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDecimal(out var n)?n.ToString(CultureInfo.InvariantCulture):fallback.ToString(CultureInfo.InvariantCulture);
    public static bool Flag(IReadOnlyDictionary<string,JsonElement> c,string key,bool fallback=false)=>c.TryGetValue(key,out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean():fallback;
    private static int[] Ids(string value)=>string.IsNullOrWhiteSpace(value)?[]:value.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(value=>int.TryParse(value,out var id)&&id>=0?id:throw new IOException("Identifiant de piste invalide.")).Distinct().ToArray();
    private static string Output(string directory,string node,string extension)=>Path.Combine(directory,"out-"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node)))[..16]+extension);

    public async Task<MediaActionOutcome> ExecuteAsync(string id,IReadOnlyDictionary<string,JsonElement> config,string current,string directory,string node,Func<string,Task<string>> extra,Guid workerId,CancellationToken ct)
    {
        if(id.StartsWith("base-probe-",StringComparison.Ordinal))
        {
            var metadata=await scanner.ScanAsync(current,ct);
            var value=FlowMediaValues.Probe(id,metadata,int.Parse(Number(config,"trackId")));
            return new(null,[],value);
        }
        if(id.StartsWith("base-inspect-",StringComparison.Ordinal)||id=="base-export-media-report")
        {
            var raw=await tools.CheckedAsync("mediainfo",["--Output=JSON",current],directory,ct);
            using var json=JsonDocument.Parse(raw); var value=json.RootElement.Clone();
            if(id=="base-export-media-report") { var file=Output(directory,node,".json"); await File.WriteAllTextAsync(file,raw,ct); return new(null,[file],value); }
            if(id!="base-inspect-media" && value.TryGetProperty("media",out var media) && media.TryGetProperty("track",out var tracks))
                value=JsonSerializer.SerializeToElement(tracks.EnumerateArray().Where(track=>id!="base-inspect-hdr"||track.TryGetProperty("@type",out var type)&&type.GetString()=="Video").Skip(int.Parse(Number(config,"trackId"))).Take(1));
            return new(null,[],value);
        }
        if(id is "base-transcode-video" or "base-transcode-audio" or "base-normalize-audio" or "base-resize-video" or "base-remux-media")
            return await FfmpegPlanAsync([new(id,config,node)],current,directory,node,workerId,ct);
        if(id=="base-verify-decoding")
        {
            var args=new List<string> { "-nostdin","-v","error","-xerror","-i",current };
            var selected=Ids(Text(config,"trackIds"));
            if(Text(config,"language")!="")
            {
                var metadata=await scanner.ScanAsync(current,ct);
                selected=metadata.GetProperty("streams").EnumerateArray().Where(s=>s.TryGetProperty("tags",out var tags)&&tags.TryGetProperty("language",out var language)&&language.GetString()==Text(config,"language")
                    && (selected.Length==0||selected.Contains(s.GetProperty("index").GetInt32()))
                    && (Text(config,"trackType","all")=="all" || s.GetProperty("codec_type").GetString()==(Text(config,"trackType")=="subtitles"?"subtitle":Text(config,"trackType"))))
                    .Select(s=>s.GetProperty("index").GetInt32()).ToArray();
                if(selected.Length==0) throw new MediaToolException("sélection de pistes",1);
            }
            if(selected.Length>0) foreach(var track in selected) args.AddRange(["-map","0:"+track]);
            else if(Text(config,"trackType","all")!="all") args.AddRange(["-map","0:"+Text(config,"trackType")[0]+"?"]);
            args.AddRange(["-f","null","-"]); await tools.CheckedAsync("ffmpeg",args,directory,ct); return new(null,[]);
        }
        if(id=="base-video-thumbnail")
        {
            var file=Output(directory,node,".jpg");
            await tools.CheckedAsync("ffmpeg",["-nostdin","-y","-v","error","-ss",Number(config,"positionSeconds"),"-i",current,"-frames:v","1","-vf","scale="+Number(config,"width",1280)+":"+Number(config,"height",720)+":force_original_aspect_ratio=decrease",file],directory,ct);
            return new(null,[file]);
        }
        if(id=="base-extract-audio")
        {
            var codec=Text(config,"audioCodec","copy"); var file=Output(directory,node,codec=="flac"?".flac":codec=="opus"?".opus":".mka");
            await tools.CheckedAsync("ffmpeg",["-nostdin","-y","-v","error","-i",current,"-map","0:a:"+Number(config,"trackId"),"-vn","-c:a",codec=="opus"?"libopus":codec,file],directory,ct); return new(null,[file]);
        }
        if(id=="base-extract-subtitles")
        {
            var metadata=await scanner.ScanAsync(current,ct); var selected=Ids(Text(config,"trackIds")); var files=new List<string>();
            foreach(var stream in metadata.GetProperty("streams").EnumerateArray().Where(s=>s.GetProperty("codec_type").GetString()=="subtitle" && (selected.Length==0||selected.Contains(s.GetProperty("index").GetInt32()))))
            {
                var file=Output(directory,node+stream.GetProperty("index").GetInt32(),stream.GetProperty("codec_name").GetString()=="ass"?".ass":".srt");
                await tools.CheckedAsync("ffmpeg",["-nostdin","-y","-v","error","-i",current,"-map","0:"+stream.GetProperty("index").GetInt32(),file],directory,ct); files.Add(file);
            }
            return new(null,files);
        }
        if(id is "base-convert-image" or "base-resize-image" or "base-crop-image")
        {
            var file=Output(directory,node,id=="base-convert-image"?"."+Text(config,"format","webp"):Path.GetExtension(current));
            var exe=ProcessToolRunner.Find("magick") is not null?"magick":"convert"; var args=new List<string> { current };
            if(id=="base-convert-image") args.AddRange(["-quality",Number(config,"quality",85)]);
            if(id=="base-resize-image") args.AddRange(["-resize",Number(config,"width",1920)+"x"+Number(config,"height",1080)+(Flag(config,"preserveAspectRatio",true)?">":"!")]);
            if(id=="base-crop-image")
            {
                var ratio=Text(config,"aspectRatio");
                if(ratio!="")
                {
                    var parts=ratio.Split(':');
                    if(parts.Length!=2 || !int.TryParse(parts[0],out var a)||!int.TryParse(parts[1],out var b)||a<=0||b<=0) throw new MediaToolException("proportions de recadrage",1);
                    var scan=await scanner.ScanAsync(current,ct); var image=scan.GetProperty("streams")[0]; var w=image.GetProperty("width").GetInt32(); var h=image.GetProperty("height").GetInt32();
                    var cropW=Math.Min(w,(int)((long)h*a/b)); var cropH=Math.Min(h,(int)((long)w*b/a));
                    args.AddRange(["-gravity","center","-crop",cropW+"x"+cropH+"+0+0","+repage"]);
                }
                else args.AddRange(["-crop",Number(config,"width",1920)+"x"+Number(config,"height",1080)+"+"+Number(config,"x")+"+"+Number(config,"y"),"+repage"]);
            }
            args.Add(file); await tools.CheckedAsync(exe,args,directory,ct); return new(file,[]);
        }
        return await MatroskaAsync(id,config,current,directory,node,extra,ct);
    }

    public async Task<MediaActionOutcome> FfmpegPlanAsync(IReadOnlyList<RuntimePlanStep> plan,string current,string directory,string node,Guid workerId,CancellationToken ct,bool preserve=true)
    {
        var output=Output(directory,node,Path.GetExtension(current) is ".mp4" or ".mkv" or ".mka" or ".flac"?Path.GetExtension(current):".mkv");
        var input=new List<string> { "-nostdin","-y","-v","error" }; var args=new List<string> { "-c","copy" }; var videoFilters=new List<string>(); var hardwareMode="cpu";
        var resolutions=new List<ResolvedVideoEncoding>();
        if(preserve || !plan.Any(p=>p.ComponentId is "base-transcode-video" or "base-transcode-audio" or "base-normalize-audio" or "base-resize-video")) args.AddRange(["-map","0"]);
        else {
            if(plan.Any(p=>p.ComponentId is "base-transcode-video" or "base-resize-video")) args.AddRange(["-map","0:v?"]);
            foreach(var track in plan.Where(p=>p.ComponentId is "base-transcode-audio" or "base-normalize-audio").Select(p=>Number(p.Configuration,"trackId")).Distinct()) args.AddRange(["-map","0:a:"+track]);
        }
        foreach(var step in plan)
        {
            var c=step.Configuration;
            switch(step.ComponentId)
            {
                case "base-transcode-video":
                {
                    var codec=Text(c,"videoCodec","hevc");
                    var ffmpeg=(await capabilities.DiscoverAsync(ct)).FirstOrDefault(capability=>capability.ToolId=="ffmpeg")
                        ??throw new MediaToolException("FFmpeg",1);
                    if(c.TryGetValue("encodingProfile",out var profileValue) && profileValue.ValueKind==JsonValueKind.Object)
                    {
                        var profile=profileValue.Deserialize<EncodingProfileSnapshot>(new JsonSerializerOptions(JsonSerializerDefaults.Web))
                            ??throw new MediaToolException("profil d’encodage",1);
                        var mode=Text(c,"encoderMode","automatic");
                        var selected=Text(c,"encoderId");
                        var resolved=MediaCodecPolicy.ResolveVideoEncoder(codec,mode,selected,profile,ffmpeg.Encoders??[],ffmpeg.AccelerationTypes,ffmpeg.EncoderChecks)
                            ??throw new MediaToolException("encodeur vidéo compatible avec le profil",1);
                        var encoder=resolved.Encoder;
                        var target=resolved.Target;
                        hardwareMode=encoder.Acceleration;
                        if(hardwareMode=="vaapi") input.AddRange(["-vaapi_device",GpuDeviceSelector.RenderDevice()]);
                        args.AddRange(["-c:v",encoder.Id,encoder.QualityOption,target.Quality.ToString(CultureInfo.InvariantCulture)]);
                        var preset=encoder.Presets.FirstOrDefault(candidate=>candidate.Id==target.Preset)?.Value;
                        if(!string.IsNullOrEmpty(encoder.PresetOption) && !string.IsNullOrEmpty(preset))
                            args.AddRange([encoder.PresetOption,preset]);
                        // Older durable plans predate FlowNodeId. Keep them executable, but do not
                        // record a misleading resolution under the enclosing processing node.
                        if(step.FlowNodeId is { } flowNodeId)
                            resolutions.Add(new(flowNodeId,workerId,codec,encoder.Id,ffmpeg.Version,encoder.Acceleration,
                                encoder.QualityMode,encoder.QualityOption,target.Quality,target.Preset,
                                string.IsNullOrEmpty(encoder.PresetOption)?null:encoder.PresetOption,preset,
                                profile.Id,profile.Version,profile.Name,target.Priority,DateTimeOffset.UtcNow));
                    }
                    else
                    {
                        var hardware=Text(c,"hardwareAcceleration","cpu"); hardwareMode=hardware;
                        var available=ffmpeg.Encoders??[];
                        var encoder=MediaCodecPolicy.VideoEncoder(codec,hardware,available)??throw new MediaToolException("encodeur vidéo",1);
                        if(hardware=="vaapi") input.AddRange(["-vaapi_device",GpuDeviceSelector.RenderDevice()]);
                        args.AddRange(["-c:v",encoder,hardware=="cpu"?"-crf":hardware=="nvenc"?"-cq":hardware=="qsv"?"-global_quality":"-qp",Number(c,"quality",23)]);
                        var preset=Text(c,"preset","medium");
                        if(encoder=="libsvtav1") preset=preset=="slow"?"4":preset=="fast"?"10":"8";
                        if(hardware=="nvenc") preset=preset=="slow"?"p7":preset=="fast"?"p1":"p4";
                        if(hardware!="vaapi") args.AddRange(["-preset",preset]);
                    }
                    break;
                }
                case "base-transcode-audio":
                    var index=Number(c,"trackId"); var audio=Text(c,"audioCodec","aac"); args.AddRange(["-c:a:"+index,audio=="opus"?"libopus":audio]);
                    if(audio!="flac") args.AddRange(["-b:a:"+index,Number(c,"bitrateKbps",192)+"k"]);
                    if(Path.GetExtension(current)==".flac" && audio!="flac" || Path.GetExtension(current)==".mp4" && audio=="flac") output=Output(directory,node,".mkv");
                    if(Text(c,"channels","source")!="source") args.AddRange(["-ac:a:"+index,Text(c,"channels")]); break;
                case "base-normalize-audio": if(Path.GetExtension(current)==".flac") output=Output(directory,node,".mka"); args.AddRange(["-filter:a:"+Number(c,"trackId"),"loudnorm=I="+Number(c,"targetLoudness",-16)+":TP="+Number(c,"truePeak",-1),"-c:a:"+Number(c,"trackId"),"aac"]); break;
                case "base-resize-video": videoFilters.Add("scale="+Number(c,"width",1920)+":"+Number(c,"height",1080)+":force_original_aspect_ratio=decrease"); if(!plan.Any(p=>p.ComponentId=="base-transcode-video")) args.AddRange(["-c:v","libx264"]); break;
                case "base-remux-media": output=Output(directory,node,"."+Text(c,"container","mkv")); break;
                default: throw new IOException("Ce réglage ne fait pas partie d’un plan FFmpeg.");
            }
        }
        if(hardwareMode=="vaapi") videoFilters.AddRange(["format=nv12","hwupload"]);
        if(videoFilters.Count>0) args.AddRange(["-vf",string.Join(',',videoFilters)]);
        input.AddRange(["-i",current]); input.AddRange(args); input.Add(output); await tools.CheckedAsync("ffmpeg",input,directory,ct); return new(output,[],null,resolutions);
    }

    private async Task<MediaActionOutcome> MatroskaAsync(string id,IReadOnlyDictionary<string,JsonElement> c,string current,string directory,string node,Func<string,Task<string>> extra,CancellationToken ct)
    {
        var file=Output(directory,node,".mkv");
        if(id is "base-extract-track" or "base-extract-attachments" or "base-extract-chapters")
        {
            if(id=="base-extract-chapters")
            {
                var simple=Text(c,"format","xml")!="xml"; var args=new List<string> { current,"chapters" }; if(simple) args.Add("--simple");
                file=Output(directory,node,simple?".txt":".xml"); var text=await tools.CheckedAsync("mkvextract",args,directory,ct); await File.WriteAllTextAsync(file,text,ct); return new(null,[file]);
            }
            var tracks=id=="base-extract-track"?[int.Parse(Number(c,"trackId"))]:Ids(Text(c,"attachmentIds"));
            if(tracks.Length==0)
            {
                var identification=await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct); using var json=JsonDocument.Parse(identification);
                tracks=json.RootElement.GetProperty("attachments").EnumerateArray().Select(a=>a.GetProperty("id").GetInt32()).ToArray();
            }
            var args2=new List<string> { current,id=="base-extract-track"?"tracks":"attachments" }; var files=new List<string>();
            foreach(var track in tracks) { var result=Output(directory,node+track,".bin"); files.Add(result); args2.Add(track+":"+result); }
            await tools.CheckedAsync("mkvextract",args2,directory,ct); return new(null,files);
        }
        if(id is "base-edit-track-properties" or "base-edit-matroska-metadata" or "base-remove-attachments" or "base-replace-chapters")
        {
            File.Copy(current,file,true); var args=new List<string> { file };
            if(id=="base-edit-matroska-metadata") args.AddRange(["--edit","info","--set","title="+Text(c,"title")]);
            if(id=="base-edit-track-properties")
            {
                var raw=await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct); using var identification=JsonDocument.Parse(raw);
                var track=identification.RootElement.GetProperty("tracks").EnumerateArray().Single(t=>t.GetProperty("id").GetInt32()==int.Parse(Number(c,"trackId")));
                args.AddRange(["--edit","track:@"+track.GetProperty("properties").GetProperty("uid").GetUInt64()]);
                foreach(var pair in new[]{("language","language"),("trackName","name")}) if(Text(c,pair.Item1)!="") args.AddRange(["--set",pair.Item2+"="+Text(c,pair.Item1)]);
                args.AddRange(["--set","flag-default="+(Flag(c,"defaultTrack")?"1":"0"),"--set","flag-forced="+(Flag(c,"forcedTrack")?"1":"0")]);
            }
            if(id=="base-replace-chapters") args.AddRange(["--chapters",await extra(Text(c,"sourceFile"))]);
            if(id=="base-remove-attachments")
            {
                var selected=Ids(Text(c,"attachmentIds"));
                if(selected.Length==0) { await tools.CheckedAsync("mkvmerge",["-o",file+".next.mkv","--no-attachments",current],directory,ct); File.Move(file+".next.mkv",file,true); return new(file,[]); }
                foreach(var attachment in selected) args.AddRange(["--delete-attachment",attachment.ToString()]);
            }
            await tools.CheckedAsync("mkvpropedit",args,directory,ct); return new(file,[]);
        }
        var merge=new List<string> { "-o",file };
        if(id=="base-reorder-tracks") merge.AddRange(["--track-order",string.Join(',',Ids(Text(c,"trackOrder")).Select(track=>"0:"+track))]);
        if(id is "base-remux-matroska" or "base-remove-tracks")
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
            if(ids.Length>0) foreach(var type in types) merge.AddRange(["--"+(type=="subtitles"?"subtitle":type)+"-tracks",(id=="base-remove-tracks"?"!":"")+string.Join(',',ids)]);
            if(id=="base-remove-tracks" && ids.Length==0) foreach(var type in types) merge.Add("--no-"+type);
            if(Text(c,"title")!="") merge.AddRange(["--title",Text(c,"title")]);
            if(id=="base-remux-matroska" && !Flag(c,"keepAttachments",true)) merge.Add("--no-attachments");
        }
        if(id is not("base-remux-matroska" or "base-remove-tracks" or "base-reorder-tracks" or "base-add-audio-track" or "base-add-subtitle-track")) throw new IOException("Action média inconnue.");
        merge.Add(current);
        if(id is "base-add-audio-track" or "base-add-subtitle-track")
        {
            if(Text(c,"language")!="") merge.AddRange(["--language","0:"+Text(c,"language")]);
            if(Text(c,"trackName")!="") merge.AddRange(["--track-name","0:"+Text(c,"trackName")]);
            merge.AddRange([id=="base-add-audio-track"?"--default-track-flag":"--forced-display-flag","0:"+(Flag(c,id=="base-add-audio-track"?"defaultTrack":"forcedTrack")?"1":"0")]);
            merge.Add(await extra(Text(c,"sourceFile")));
        }
        await tools.CheckedAsync("mkvmerge",merge,directory,ct); return new(file,[]);
    }
}
