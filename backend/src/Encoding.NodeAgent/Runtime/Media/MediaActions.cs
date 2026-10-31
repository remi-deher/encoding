using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed record MediaActionOutcome(string? Primary,IReadOnlyList<string> Additional,JsonElement? Value=null,
    IReadOnlyList<ResolvedVideoEncoding>? VideoEncodings=null,bool NeedsChanges=false);

public sealed partial class MediaActions(ProcessToolRunner tools,MediaScanner scanner,IToolCapabilityProbe capabilities,ILogger<MediaActions>? logger=null)
{
    public static string Text(IReadOnlyDictionary<string,JsonElement> c,string key,string fallback="")=>c.TryGetValue(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:fallback;
    public static string Number(IReadOnlyDictionary<string,JsonElement> c,string key,decimal fallback=0)=>c.TryGetValue(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDecimal(out var n)?n.ToString(CultureInfo.InvariantCulture):fallback.ToString(CultureInfo.InvariantCulture);
    public static bool Flag(IReadOnlyDictionary<string,JsonElement> c,string key,bool fallback=false)=>c.TryGetValue(key,out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean():fallback;
    private static int[] Ids(string value)=>string.IsNullOrWhiteSpace(value)?[]:value.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(value=>int.TryParse(value,out var id)&&id>=0?id:throw new IOException("Identifiant de piste invalide.")).Distinct().ToArray();
    private static string Output(string directory,string node,string extension)=>Path.Combine(directory,"out-"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node)))[..16]+extension);

    public async Task<MediaActionOutcome> ExecuteAsync(string id,IReadOnlyDictionary<string,JsonElement> config,string current,string directory,string node,Func<string,Task<string>> extra,Guid workerId,CancellationToken ct,JsonElement? knownMetadata=null)
    {
        if(id.StartsWith("base-probe-",StringComparison.Ordinal) || id.StartsWith("probe-",StringComparison.Ordinal))
        {
            if(id=="probe-validate-output") throw new InvalidOperationException("Le validateur de sortie doit recevoir les métadonnées source et résultat.");
            var metadata=await scanner.ScanAsync(current,ct);
            var value=FlowMediaValues.Probe(id,metadata,int.Parse(Number(config,"trackId")));
            return new(null,[],value);
        }
        if(id.StartsWith("base-inspect-",StringComparison.Ordinal) || id.StartsWith("probe-inspect-",StringComparison.Ordinal) || id is "base-export-media-report" or "probe-export-report")
        {
            var raw=await tools.CheckedAsync("mediainfo",["--Output=JSON",current],directory,ct);
            using var json=JsonDocument.Parse(raw); var value=json.RootElement.Clone();
            if(id is "base-export-media-report" or "probe-export-report") { var file=Output(directory,node,".json"); await File.WriteAllTextAsync(file,raw,ct); return new(null,[file],value); }
            if(id is not ("base-inspect-media" or "probe-inspect-media") && value.TryGetProperty("media",out var media) && media.TryGetProperty("track",out var tracks))
                value=JsonSerializer.SerializeToElement(tracks.EnumerateArray().Where(track=>id is not ("base-inspect-hdr" or "probe-inspect-hdr")||track.TryGetProperty("@type",out var type)&&type.GetString()=="Video").Skip(int.Parse(Number(config,"trackId"))).Take(1));
            return new(null,[],value);
        }
        if(id is "base-transcode-video" or "video-transcode" or "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize" or "base-resize-video" or "video-resize" or "base-remux-media" or "video-remux-media" or "base-auto-crop" or "video-auto-crop" or "base-deinterlace-video" or "video-deinterlace")
            return await FfmpegPlanAsync([new(id,config,node)],current,directory,node,workerId,ct,knownMetadata:knownMetadata);
        if(id is "base-verify-decoding" or "video-verify-decoding")
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
            args.AddRange(["-fps_mode","vfr","-f","null","-"]); await tools.CheckedAsync("ffmpeg",args,directory,ct); return new(null,[]);
        }
        if(id is "base-video-thumbnail" or "video-thumbnail")
        {
            var file=Output(directory,node,".jpg");
            await tools.CheckedAsync("ffmpeg",["-nostdin","-y","-v","error","-ss",Number(config,"positionSeconds"),"-i",current,"-frames:v","1","-vf","scale="+Number(config,"width",1280)+":"+Number(config,"height",720)+":force_original_aspect_ratio=decrease",file],directory,ct);
            return new(null,[file]);
        }
        if(id is "base-extract-audio" or "audio-extract")
        {
            var codec=Text(config,"audioCodec","copy"); var file=Output(directory,node,codec=="flac"?".flac":codec=="opus"?".opus":".mka");
            await tools.CheckedAsync("ffmpeg",["-nostdin","-y","-v","error","-i",current,"-map","0:a:"+Number(config,"trackId"),"-vn","-c:a",codec=="opus"?"libopus":codec,file],directory,ct); return new(null,[file]);
        }
        if(id is "base-extract-subtitles" or "subtitles-extract")
        {
            var metadata=await scanner.ScanAsync(current,ct); var selected=Ids(Text(config,"trackIds")); var files=new List<string>();
            foreach(var stream in metadata.GetProperty("streams").EnumerateArray().Where(s=>s.GetProperty("codec_type").GetString()=="subtitle" && (selected.Length==0||selected.Contains(s.GetProperty("index").GetInt32()))))
            {
                var file=Output(directory,node+stream.GetProperty("index").GetInt32(),stream.GetProperty("codec_name").GetString()=="ass"?".ass":".srt");
                await tools.CheckedAsync("ffmpeg",["-nostdin","-y","-v","error","-i",current,"-map","0:"+stream.GetProperty("index").GetInt32(),file],directory,ct); files.Add(file);
            }
            return new(null,files);
        }
        if(id is "base-convert-image" or "base-resize-image" or "base-crop-image" or "image-convert" or "image-resize" or "image-crop")
        {
            var file=Output(directory,node,(id is "base-convert-image" or "image-convert")?"."+Text(config,"format","webp"):Path.GetExtension(current));
            var exe=ProcessToolRunner.Find("magick") is not null?"magick":"convert"; var args=new List<string> { current };
            if(id is "base-convert-image" or "image-convert") args.AddRange(["-quality",Number(config,"quality",85)]);
            if(id is "base-resize-image" or "image-resize") args.AddRange(["-resize",Number(config,"width",1920)+"x"+Number(config,"height",1080)+(Flag(config,"preserveAspectRatio",true)?">":"!")]);
            if(id is "base-crop-image" or "image-crop")
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
}
