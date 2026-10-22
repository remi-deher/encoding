using System.Globalization;
using System.Text.Json;
using Encoding.Contracts.Flows;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;
using Microsoft.Extensions.Logging;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed record MediaActionOutcome(string? Primary,IReadOnlyList<string> Additional,JsonElement? Value=null,
    IReadOnlyList<ResolvedVideoEncoding>? VideoEncodings=null);

public sealed partial class MediaActions(ProcessToolRunner tools,MediaScanner scanner,IToolCapabilityProbe capabilities,ILogger<MediaActions>? logger=null)
{
    public static string Text(IReadOnlyDictionary<string,JsonElement> c,string key,string fallback="")=>c.TryGetValue(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:fallback;
    public static string Number(IReadOnlyDictionary<string,JsonElement> c,string key,decimal fallback=0)=>c.TryGetValue(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDecimal(out var n)?n.ToString(CultureInfo.InvariantCulture):fallback.ToString(CultureInfo.InvariantCulture);
    public static bool Flag(IReadOnlyDictionary<string,JsonElement> c,string key,bool fallback=false)=>c.TryGetValue(key,out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean():fallback;
    private static int[] Ids(string value)=>string.IsNullOrWhiteSpace(value)?[]:value.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(value=>int.TryParse(value,out var id)&&id>=0?id:throw new IOException("Identifiant de piste invalide.")).Distinct().ToArray();
    private static string Output(string directory,string node,string extension)=>Path.Combine(directory,"out-"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node)))[..16]+extension);

    public async Task<MediaActionOutcome> ExecuteAsync(string id,IReadOnlyDictionary<string,JsonElement> config,string current,string directory,string node,Func<string,Task<string>> extra,Guid workerId,CancellationToken ct)
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
            return await FfmpegPlanAsync([new(id,config,node)],current,directory,node,workerId,ct);
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
            args.AddRange(["-f","null","-"]); await tools.CheckedAsync("ffmpeg",args,directory,ct); return new(null,[]);
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

    /// <summary>Nomenclature des pistes (0056). Mode « snapshot » : retourne les titres d'origine sans toucher au fichier. Mode « apply » : mkvpropedit sur une copie de travail ; aucun fichier n'est produit quand rien ne change ou que le conteneur n'est pas Matroska.</summary>
    public async Task<MediaActionOutcome> TrackNamesAsync(IReadOnlyDictionary<string,JsonElement> c,JsonElement probe,JsonElement? origin,string current,string directory,string node,CancellationToken ct)
    {
        if(Path.GetExtension(current)!=".mkv") return new(null,[]);
        using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
        if(Text(c,"mode","apply")=="snapshot") return new(null,[],TrackNaming.Snapshot(identification.RootElement).Clone());
        var edits=TrackNaming.Plan(identification.RootElement,probe,origin,TrackNameOptions.From(c));
        if(edits.Count==0) return new(null,[]);
        var file=Output(directory,node,".mkv"); File.Copy(current,file,true); var args=new List<string> { file };
        foreach(var edit in edits)
        {
            args.AddRange(["--edit","track:="+edit.Uid,"--set","name="+edit.Name]);
            if(edit.DefaultFlag is { } flag) args.AddRange(["--set","flag-default="+(flag?"1":"0")]);
        }
        await tools.CheckedAsync("mkvpropedit",args,directory,ct); return new(file,[],JsonSerializer.SerializeToElement(edits.Select(edit=>edit.Note).ToArray()));
    }

    /// <summary>Gestion des sous-titres (0057) : extrait les pistes texte, planifie (conversion SRT vers ASS, mise à niveau, doublons, forcés, titres, défaut) puis écrit un seul MKV avec mkvmerge. Les pistes non modifiées sont recopiées de la source ; rien n'est produit quand tout est déjà conforme.</summary>
    public async Task<MediaActionOutcome> SubtitleManageAsync(IReadOnlyDictionary<string,JsonElement> c,JsonElement probe,string current,string directory,string node,CancellationToken ct)
    {
        if(Path.GetExtension(current)!=".mkv") return new(null,[]);
        var options=SubtitleOptions.From(c);
        var streams=probe.GetProperty("streams").EnumerateArray().ToArray();
        string Kind(JsonElement s)=>s.TryGetProperty("codec_type",out var t)?t.GetString()??"":"";
        string Tag(JsonElement s,string name)=>s.TryGetProperty("tags",out var tags)&&tags.ValueKind==JsonValueKind.Object&&tags.TryGetProperty(name,out var v)?v.GetString()??"":"";
        bool Disposition(JsonElement s,string name)=>s.TryGetProperty("disposition",out var d)&&d.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.GetInt32()==1;
        var subtitleStreams=streams.Where(s=>Kind(s)=="subtitle").ToArray();
        if(subtitleStreams.Length==0) return new(null,[]);
        using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
        var allTracks=identification.RootElement.GetProperty("tracks").EnumerateArray().ToArray();
        var subtitleIds=allTracks.Where(t=>t.GetProperty("type").GetString()=="subtitles").Select(t=>t.GetProperty("id").GetInt32()).ToArray();
        if(subtitleIds.Length!=subtitleStreams.Length) throw new SubtitleException("Les pistes de sous-titres de FFprobe et de MKVToolNix ne correspondent pas : fichier laissé intact.");
        (int Width,int Height) canvas=(0,0);
        if(options.ApplyProfile)
        {
            var video=streams.FirstOrDefault(s=>Kind(s)=="video"&&!Disposition(s,"attached_pic"));
            if(video.ValueKind!=JsonValueKind.Object) throw new SubtitleException("Aucune piste vidéo : profil de sous-titres adaptatif impossible.");
            canvas=AssProfile.Canvas(video);
        }
        var work=Path.Combine(directory,"subtitles-"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node)))[..16]);
        Directory.CreateDirectory(work);
        try
        {
            var raw=new List<(int Ordinal,int Id,string File,bool Srt)>(); var texts=new Dictionary<int,string>(); var empty=new HashSet<int>();
            for(var ordinal=0;ordinal<subtitleStreams.Length;ordinal++)
            {
                var stream=subtitleStreams[ordinal]; var codec=(stream.TryGetProperty("codec_name",out var name)?name.GetString()??"":"").ToLowerInvariant();
                if(!SubtitlePlanner.IsText(codec)) continue;
                if(codec is "subrip" or "srt" or "ass" or "ssa") raw.Add((ordinal,subtitleIds[ordinal],Path.Combine(work,ordinal+(codec is "subrip" or "srt"?".srt":".ass")),codec is "subrip" or "srt"));
                else
                {
                    var file=Path.Combine(work,ordinal+".ass");
                    await tools.CheckedAsync("ffmpeg",["-nostdin","-hide_banner","-loglevel","error","-y","-i",current,"-map","0:"+stream.GetProperty("index").GetInt32(),"-c:s","ass",file],directory,ct);
                    texts[ordinal]=await File.ReadAllTextAsync(file,ct);
                }
            }
            if(raw.Count>0) await tools.CheckedAsync("mkvextract",["tracks",current,..raw.Select(item=>item.Id+":"+item.File)],directory,ct);
            foreach(var item in raw)
            {
                if(!File.Exists(item.File)) throw new SubtitleException($"Extraction de la piste {item.Ordinal} impossible : fichier laissé intact.");
                if(!item.Srt) { texts[item.Ordinal]=await File.ReadAllTextAsync(item.File,ct); continue; }
                if(SubtitlePlanner.SrtHasNoVisibleText(await File.ReadAllTextAsync(item.File,ct))) { empty.Add(item.Ordinal); texts[item.Ordinal]="[Events]\n"; continue; }
                var converted=Path.Combine(work,item.Ordinal+"-converted.ass");
                await tools.CheckedAsync("ffmpeg",["-nostdin","-hide_banner","-loglevel","error","-y","-i",item.File,"-c:s","ass",converted],directory,ct);
                texts[item.Ordinal]=await File.ReadAllTextAsync(converted,ct);
            }
            var tracks=new List<SubtitleTrack>();
            for(var ordinal=0;ordinal<subtitleStreams.Length;ordinal++)
            {
                var stream=subtitleStreams[ordinal]; var properties=allTracks.First(t=>t.GetProperty("id").GetInt32()==subtitleIds[ordinal]).GetProperty("properties");
                bool Property(string name)=>properties.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
                tracks.Add(new(ordinal,stream.TryGetProperty("codec_name",out var codec)?codec.GetString()??"":"",Tag(stream,"language"),Tag(stream,"title"),Disposition(stream,"default"),
                    Disposition(stream,"forced")||Property("forced_track"),Disposition(stream,"hearing_impaired")||Property("flag_hearing_impaired"),
                    texts.TryGetValue(ordinal,out var text)?text:null,empty.Contains(ordinal)));
            }
            var plan=SubtitlePlanner.Plan(tracks,SubtitlePlanner.ReferenceAudioIsFrench(probe),canvas,options);
            var lines=plan.Notes.Concat(plan.Decisions.Select(d=>$"Sous-titre {d.Ordinal} : {(d.Drop?"retiré":d.ReplacementAss is not null?d.Note:"conservé")}, « {d.Title} »{(d.Default?", par défaut":"")}{(d.Forced?", forcé":"")}")).ToArray();
            if(!plan.Changed) return new(null,[],JsonSerializer.SerializeToElement(plan.Notes.ToArray()));
            var file2=Output(directory,node,".mkv"); var args=new List<string> { "-o",file2 };
            var kept=plan.Decisions.Where(d=>!d.Drop&&d.ReplacementAss is null).ToArray();
            if(kept.Length==0) args.Add("--no-subtitles"); else args.AddRange(["--subtitle-tracks",string.Join(',',kept.Select(d=>subtitleIds[d.Ordinal]))]);
            foreach(var d in kept)
            {
                var id=subtitleIds[d.Ordinal]; var original=tracks[d.Ordinal];
                if(d.Title!=original.Title&&d.Title.Length>0) args.AddRange(["--track-name",id+":"+d.Title]);
                if(d.Language!=original.Language&&d.Language.Length>0) args.AddRange(["--language",id+":"+d.Language]);
                args.AddRange(["--default-track-flag",id+":"+(d.Default?"1":"0"),"--forced-display-flag",id+":"+(d.Forced?"1":"0")]);
            }
            args.Add(current);
            var order=new List<string>(); var replacements=new Dictionary<int,int>(); var fileIndex=1;
            foreach(var d in plan.Decisions.Where(d=>!d.Drop&&d.ReplacementAss is not null))
            {
                var replacement=Path.Combine(work,"replacement-"+d.Ordinal+".ass");
                await File.WriteAllTextAsync(replacement,d.ReplacementAss!,new System.Text.UTF8Encoding(false),ct);
                if(d.Title.Length>0) args.AddRange(["--track-name","0:"+d.Title]);
                if(d.Language.Length>0&&d.Language!="und") args.AddRange(["--language","0:"+d.Language]);
                args.AddRange(["--default-track-flag","0:"+(d.Default?"1":"0"),"--forced-display-flag","0:"+(d.Forced?"1":"0"),replacement]);
                replacements[d.Ordinal]=fileIndex++;
            }
            foreach(var track in allTracks)
            {
                var id=track.GetProperty("id").GetInt32();
                if(track.GetProperty("type").GetString()!="subtitles") { order.Add("0:"+id); continue; }
                var ordinal=Array.IndexOf(subtitleIds,id); var decision=plan.Decisions[ordinal];
                if(decision.Drop) continue;
                order.Add(replacements.TryGetValue(ordinal,out var index)?index+":0":"0:"+id);
            }
            args.AddRange(["--track-order",string.Join(',',order)]);
            await tools.CheckedAsync("mkvmerge",args,directory,ct);
            return new(file2,[],JsonSerializer.SerializeToElement(lines));
        }
        finally { Directory.Delete(work,true); }
    }

    /// <summary>Polices (0057) : ajoute en pièces jointes les polices d'un jeu du dossier déclaré par le nœud (<c>ENCODING_FONT_DIR</c>) qui ne sont pas déjà dans le MKV. Jamais de chemin libre.</summary>
    public async Task<MediaActionOutcome> AddFontsAsync(IReadOnlyDictionary<string,JsonElement> c,string current,string directory,string node,CancellationToken ct,string? resourceFolder=null)
    {
        if(Path.GetExtension(current)!=".mkv") return new(null,[]);
        var extensions = new[] { ".ttf", ".otf", ".ttc" };
        string folder,set; List<string> names;
        if(resourceFolder is not null)
        {
            // Ressource du catalogue (ADR 0006) : déjà téléchargée et vérifiée par le runner ; toutes ses polices sont ajoutées.
            folder=resourceFolder; set="ressource";
            names=Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(name=>extensions.Contains(Path.GetExtension(name)!.ToLowerInvariant())).Order(StringComparer.Ordinal).ToList()!;
            if(names.Count==0) throw new SubtitleException("La ressource ne contient aucune police.");
        }
        else
        {
        var root=Path.GetFullPath(Environment.GetEnvironmentVariable("ENCODING_FONT_DIR") is { Length: >0 } configured?configured:"/app/common/Fonts");
        set=Text(c,"fontSet","Crunchyroll");
        if(set.Length==0||!System.Text.RegularExpressions.Regex.IsMatch(set,@"^[A-Za-z0-9][A-Za-z0-9 ._-]*$")||set.Contains("..",StringComparison.Ordinal)) throw new SubtitleException("Nom de jeu de polices invalide.");
        folder=Path.GetFullPath(Path.Combine(root,set));
        if(!folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.Ordinal)||!Directory.Exists(folder)) throw new SubtitleException($"Jeu de polices « {set} » introuvable sur ce nœud.");
        names=Text(c,"fonts").Split([',','\n',';'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList();
        if(names.Count==0) names=Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(name=>extensions.Contains(Path.GetExtension(name)!.ToLowerInvariant())).Order(StringComparer.Ordinal).ToList()!;
        if(names.Count==0) throw new SubtitleException($"Le jeu de polices « {set} » ne contient aucune police.");
        }
        foreach(var name in names)
            if(name!=Path.GetFileName(name)||!extensions.Contains(Path.GetExtension(name).ToLowerInvariant())||!File.Exists(Path.Combine(folder,name)))
                throw new SubtitleException($"Police « {name} » introuvable dans le jeu « {set} ».");
        using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
        var present=identification.RootElement.TryGetProperty("attachments",out var attachments)
            ?attachments.EnumerateArray().Select(a=>(a.GetProperty("file_name").GetString()??"").ToLowerInvariant()).ToHashSet():[];
        var missing=names.Where(name=>!present.Contains(name.ToLowerInvariant())).ToArray();
        if(missing.Length==0) return new(null,[]);
        var file=Output(directory,node,".mkv"); var args=new List<string> { "-o",file };
        foreach(var name in missing) args.AddRange(["--attach-file",Path.Combine(folder,name)]);
        args.Add(current);
        await tools.CheckedAsync("mkvmerge",args,directory,ct);
        return new(file,[],JsonSerializer.SerializeToElement(missing));
    }

    public async Task<CropCoordinates?> DetectCropAsync(string current,string directory,int samplePoints,int sampleDurationSeconds,int threshold,int roundMultiple,double durationSeconds,CancellationToken ct)
    {
        var points=new List<double>();
        if(durationSeconds>10) points.AddRange([durationSeconds*0.1,durationSeconds*0.25,durationSeconds*0.5,durationSeconds*0.75,durationSeconds*0.9]);
        else points.Add(0);

        var lines=new List<string>();
        foreach(var p in points.Take(Math.Max(1,samplePoints)))
        {
            var args=new List<string> { "-nostdin","-v","error","-ss",p.ToString("F2",CultureInfo.InvariantCulture),"-i",current,"-frames:v","25","-vf",$"cropdetect={threshold}:16:0","-f","null","-" };
            var res=await tools.CheckedWithResultAsync("ffmpeg",args,directory,ct);
            if(!string.IsNullOrWhiteSpace(res.Error))
                lines.AddRange(res.Error.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries));
        }

        return CropDetector.CalculateConsensus(lines,roundMultiple);
    }

    public async Task<bool> DetectInterlacedAsync(string current,string directory,int frames,double thresholdPercent,CancellationToken ct)
    {
        var args=new List<string> { "-nostdin","-v","error","-i",current,"-frames:v",frames.ToString(CultureInfo.InvariantCulture),"-vf","idet","-f","null","-" };
        var res=await tools.CheckedWithResultAsync("ffmpeg",args,directory,ct);
        return DeinterlaceDetector.IsInterlaced(res.Error,thresholdPercent);
    }

    public async Task<MediaActionOutcome> FilterLanguagesAsync(IReadOnlyDictionary<string,JsonElement> c,JsonElement probe,string current,string directory,string node,CancellationToken ct)
    {
        var audioLangs=Text(c,"allowedAudioLanguages").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        var subLangs=Text(c,"allowedSubtitleLanguages").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
        var keepUnd=Flag(c,"keepUndefinedAudio",true);
        var fallbackAudio=Flag(c,"keepFirstAudioIfNoneMatch",true);
        var keepForced=Flag(c,"keepForcedSubtitles",true);

        var streams=probe.GetProperty("streams");
        var removeAudio=LanguageFilterPlanner.IdentifyTracksToRemove(streams,"audio",audioLangs,keepUnd,fallbackAudio,keepForced);
        var removeSub=LanguageFilterPlanner.IdentifyTracksToRemove(streams,"subtitles",subLangs,keepUnd,fallbackAudio,keepForced);

        var toRemove=new HashSet<int>(removeAudio.Concat(removeSub));
        if(toRemove.Count==0) return new(null,[],JsonSerializer.SerializeToElement(new { filtered=false }));

        var file=Output(directory,node,Path.GetExtension(current));
        if(Path.GetExtension(current)==".mkv")
        {
            using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
            var tracks=identification.RootElement.GetProperty("tracks").EnumerateArray().ToArray();
            var keptAudio=tracks.Where(t=>t.GetProperty("type").GetString()=="audio"&&!toRemove.Contains(t.GetProperty("id").GetInt32()))
                .Select(t=>t.GetProperty("id").GetInt32()).ToArray();
            var keptSub=tracks.Where(t=>t.GetProperty("type").GetString()=="subtitles"&&!toRemove.Contains(t.GetProperty("id").GetInt32()))
                .Select(t=>t.GetProperty("id").GetInt32()).ToArray();

            var mergeArgs=new List<string> { "-o",file };
            if(keptAudio.Length>0) mergeArgs.AddRange(["--audio-tracks",string.Join(',',keptAudio)]);
            else mergeArgs.Add("--no-audio");
            if(keptSub.Length>0) mergeArgs.AddRange(["--subtitle-tracks",string.Join(',',keptSub)]);
            else mergeArgs.Add("--no-subtitles");
            mergeArgs.Add(current);
            await tools.CheckedAsync("mkvmerge",mergeArgs,directory,ct);
        }
        else
        {
            var args=new List<string> { "-nostdin","-y","-v","error","-i",current,"-map","0:v" };
            foreach(var s in streams.EnumerateArray())
            {
                var idx=s.GetProperty("index").GetInt32();
                if(!toRemove.Contains(idx)) args.AddRange(["-map","0:"+idx]);
            }
            args.AddRange(["-c","copy",file]);
            await tools.CheckedAsync("ffmpeg",args,directory,ct);
        }

        return new(file,[],JsonSerializer.SerializeToElement(new { filtered=true,removedTracks=toRemove.ToArray() }));
    }

    public async Task<MediaActionOutcome> RemoveCommentaryAsync(IReadOnlyDictionary<string,JsonElement> c,JsonElement probe,string current,string directory,string node,CancellationToken ct)
    {
        var checkAudio=Flag(c,"removeAudioCommentary",true);
        var checkSub=Flag(c,"removeSubtitleCommentary",true);
        var custom=Text(c,"customKeywords").Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);

        var streams=probe.GetProperty("streams");
        var toRemove=CommentaryFilterPlanner.IdentifyCommentaryTracks(streams,checkAudio,checkSub,custom);
        if(toRemove.Count==0) return new(null,[],JsonSerializer.SerializeToElement(new { removed=false }));

        var file=Output(directory,node,Path.GetExtension(current));
        if(Path.GetExtension(current)==".mkv")
        {
            using var identification=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
            var tracks=identification.RootElement.GetProperty("tracks").EnumerateArray().ToArray();
            var keptAudio=tracks.Where(t=>t.GetProperty("type").GetString()=="audio"&&!toRemove.Contains(t.GetProperty("id").GetInt32()))
                .Select(t=>t.GetProperty("id").GetInt32()).ToArray();
            var keptSub=tracks.Where(t=>t.GetProperty("type").GetString()=="subtitles"&&!toRemove.Contains(t.GetProperty("id").GetInt32()))
                .Select(t=>t.GetProperty("id").GetInt32()).ToArray();

            var mergeArgs=new List<string> { "-o",file };
            if(keptAudio.Length>0) mergeArgs.AddRange(["--audio-tracks",string.Join(',',keptAudio)]);
            if(keptSub.Length>0) mergeArgs.AddRange(["--subtitle-tracks",string.Join(',',keptSub)]);
            mergeArgs.Add(current);
            await tools.CheckedAsync("mkvmerge",mergeArgs,directory,ct);
        }
        else
        {
            var args=new List<string> { "-nostdin","-y","-v","error","-i",current,"-map","0:v" };
            foreach(var s in streams.EnumerateArray())
            {
                var idx=s.GetProperty("index").GetInt32();
                if(!toRemove.Contains(idx)) args.AddRange(["-map","0:"+idx]);
            }
            args.AddRange(["-c","copy",file]);
            await tools.CheckedAsync("ffmpeg",args,directory,ct);
        }

        return new(file,[],JsonSerializer.SerializeToElement(new { removed=true,removedTracks=toRemove.ToArray() }));
    }

    public async Task<JsonElement> ValidateOutputAsync(string current,string original,JsonElement before,JsonElement after,IReadOnlyDictionary<string,JsonElement> config,string directory,CancellationToken ct)
    {
        var issues=new List<string>();
        var sourceStreams=Streams(before).Where(stream=>!IsAttachedPicture(stream)).ToArray();
        var outputStreams=Streams(after).Where(stream=>!IsAttachedPicture(stream)).ToArray();
        var sourceVideo=sourceStreams.Where(stream=>CodecType(stream)=="video").ToArray();
        var outputVideo=outputStreams.Where(stream=>CodecType(stream)=="video").ToArray();
        var sourceAudio=sourceStreams.Where(stream=>CodecType(stream)=="audio").ToArray();
        var outputAudio=outputStreams.Where(stream=>CodecType(stream)=="audio").ToArray();
        var sourceSubtitles=sourceStreams.Where(stream=>CodecType(stream)=="subtitle").ToArray();
        var outputSubtitles=outputStreams.Where(stream=>CodecType(stream)=="subtitle").ToArray();

        var requiredCodec=Text(config,"requiredVideoCodec","any");
        if(requiredCodec=="hevc-or-source")
        {
            for(var i=0;i<outputVideo.Length;i++)
                if(CodecName(outputVideo[i])!="hevc"&&(i>=sourceVideo.Length||CodecName(outputVideo[i])!=CodecName(sourceVideo[i])))
                    issues.Add("Une piste vidéo n’est ni en HEVC ni conservée dans son codec source.");
        }
        else if(requiredCodec!="any"&&outputVideo.Any(stream=>!CodecName(stream).Equals(requiredCodec,StringComparison.OrdinalIgnoreCase)))
            issues.Add("Le codec vidéo ne correspond pas au codec demandé ("+requiredCodec+").");
        if(outputVideo.Length==0) issues.Add("Aucune piste vidéo exploitable n’est présente dans la sortie.");

        var sourceDuration=MetadataNumber(before,"format","duration");
        var outputDuration=MetadataNumber(after,"format","duration");
        var durationTolerance=(double)decimal.Parse(Number(config,"durationToleranceSeconds",2),CultureInfo.InvariantCulture);
        if(sourceDuration is { } sourceLength&&outputDuration is { } outputLength&&Math.Abs(sourceLength-outputLength)>durationTolerance)
            issues.Add($"La durée diffère de {Math.Abs(sourceLength-outputLength):0.###} s (tolérance : {durationTolerance:0.###} s).");
        else if(sourceDuration is null||outputDuration is null) issues.Add("La durée source ou la durée de sortie est absente.");

        if(Flag(config,"preserveTrackCounts"))
        {
            foreach(var type in new[]{"video","audio"})
            {
                var expected=sourceStreams.Count(stream=>CodecType(stream)==type);
                var actual=outputStreams.Count(stream=>CodecType(stream)==type);
                if(expected!=actual) issues.Add($"Le nombre de pistes {type} a changé ({expected} → {actual}).");
            }
        }
        if(Flag(config,"preserveSubtitleCount")&&sourceSubtitles.Length!=outputSubtitles.Length)
            issues.Add($"Le nombre de pistes de sous-titres a changé ({sourceSubtitles.Length} → {outputSubtitles.Length}).");

        var allowedAudio=Csv(config,"allowedAudioCodecs");
        if(allowedAudio.Length>0)
            foreach(var stream in outputAudio.Where(stream=>!allowedAudio.Contains(CodecName(stream),StringComparer.OrdinalIgnoreCase)))
                issues.Add("Codec audio non autorisé : "+CodecName(stream)+".");
        var forbiddenAudio=Csv(config,"forbiddenAudioCodecs");
        foreach(var stream in outputAudio.Where(stream=>forbiddenAudio.Contains(CodecName(stream),StringComparer.OrdinalIgnoreCase)))
            issues.Add("Codec audio interdit dans la sortie : "+CodecName(stream)+".");
        var allowedSubtitles=Csv(config,"allowedSubtitleCodecs");
        if(allowedSubtitles.Length>0)
            foreach(var stream in outputSubtitles.Where(stream=>!allowedSubtitles.Contains(CodecName(stream),StringComparer.OrdinalIgnoreCase)))
                issues.Add("Codec de sous-titres non autorisé : "+CodecName(stream)+".");

        var maxChannels=int.Parse(Number(config,"maxAudioChannels",0),CultureInfo.InvariantCulture);
        if(maxChannels>0&&outputAudio.Any(stream=>MetadataInt(stream,"channels") is { } channels&&channels>maxChannels))
            issues.Add("Une piste audio dépasse la limite de "+maxChannels+" canaux.");
        if(Flag(config,"preserveAudioChannels")||Flag(config,"preserveAudioLayout"))
        {
            if(sourceAudio.Length!=outputAudio.Length) issues.Add("Le nombre de pistes audio a changé.");
            for(var i=0;i<Math.Min(sourceAudio.Length,outputAudio.Length);i++)
            {
                if(Flag(config,"preserveAudioChannels")&&MetadataInt(sourceAudio[i],"channels")!=MetadataInt(outputAudio[i],"channels"))
                    issues.Add($"Le nombre de canaux de la piste audio {i+1} a changé.");
                if(Flag(config,"preserveAudioLayout"))
                {
                    var sourceLayout=AudioLayout(sourceAudio[i]); var outputLayout=AudioLayout(outputAudio[i]);
                    if(sourceLayout.Length>0&&outputLayout.Length>0)
                    {
                        if(NormalizeLayout(sourceLayout)!=NormalizeLayout(outputLayout))
                            issues.Add($"La disposition audio de la piste {i+1} a changé ({sourceLayout} → {outputLayout}).");
                        if(HasLfe(sourceLayout)!=HasLfe(outputLayout))
                            issues.Add($"Le canal LFE de la piste audio {i+1} a changé.");
                    }
                }
            }
        }

        if(Flag(config,"preserveSubtitleMetadata"))
        {
            if(sourceSubtitles.Length!=outputSubtitles.Length) issues.Add("Le nombre de pistes de sous-titres a changé.");
            for(var i=0;i<Math.Min(sourceSubtitles.Length,outputSubtitles.Length);i++)
            {
                var source=sourceSubtitles[i]; var result=outputSubtitles[i];
                foreach(var (tag,label) in new[]{("language","langue"),("title","titre")})
                    if(Tag(source,tag)!=Tag(result,tag)) issues.Add($"La {label} de la piste de sous-titres {i+1} a changé.");
                foreach(var (flag,label) in new[]{("default","par défaut"),("forced","forcé"),("hearing_impaired","malentendant")})
                    if(Disposition(source,flag)!=Disposition(result,flag)) issues.Add($"Le marqueur {label} de la piste de sous-titres {i+1} a changé.");
            }
        }

        if(Flag(config,"preserveChapterCount"))
        {
            var sourceChapters=ChapterCount(before); var resultChapters=ChapterCount(after);
            if(sourceChapters is null||resultChapters is null) issues.Add("Les chapitres n’ont pas pu être comparés.");
            else if(sourceChapters!=resultChapters) issues.Add($"Le nombre de chapitres a changé ({sourceChapters} → {resultChapters}).");
        }

        var requiredAttachments=Csv(config,"requiredAttachmentNames");
        if(Flag(config,"preserveOriginalAttachments")||requiredAttachments.Length>0)
        {
            using var sourceIdentify=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",original],directory,ct));
            using var outputIdentify=JsonDocument.Parse(await tools.CheckedAsync("mkvmerge",["-J",current],directory,ct));
            var sourceAttachments=Attachments(sourceIdentify.RootElement);
            var outputAttachments=Attachments(outputIdentify.RootElement);
            if(Flag(config,"preserveOriginalAttachments"))
            {
                var remaining=outputAttachments.GroupBy(item=>item,StringComparer.OrdinalIgnoreCase).ToDictionary(group=>group.Key,group=>group.Count(),StringComparer.OrdinalIgnoreCase);
                foreach(var attachment in sourceAttachments)
                {
                    if(!remaining.TryGetValue(attachment,out var count)||count==0) { issues.Add("Une pièce jointe d’origine est absente de la sortie : "+attachment+"."); break; }
                    remaining[attachment]=count-1;
                }
            }
            foreach(var required in requiredAttachments)
                if(!outputAttachments.Any(item=>item.Split('|')[0].Equals(required,StringComparison.OrdinalIgnoreCase)))
                    issues.Add("Pièce jointe requise absente : "+required+".");
        }

        var maxGrowth=(double)decimal.Parse(Number(config,"maxOutputSizeGrowthPercent",0),CultureInfo.InvariantCulture);
        var sourceSize=MetadataNumber(before,"format","size"); var outputSize=MetadataNumber(after,"format","size");
        if(maxGrowth>0)
        {
            if(sourceSize is >0&&outputSize is { } size&&((size-sourceSize.Value)/sourceSize.Value*100)>maxGrowth)
                issues.Add($"La sortie dépasse la taille source de plus de {maxGrowth:0.###} %.");
            else if(sourceSize is null||outputSize is null) issues.Add("La taille source ou la taille de sortie est absente.");
        }

        if(Flag(config,"compareAssEvents"))
        {
            var sourceText=sourceSubtitles.Where(IsTextSubtitle).ToArray(); var outputText=outputSubtitles.Where(IsTextSubtitle).ToArray();
            if(sourceText.Length!=outputText.Length) issues.Add("Le nombre de pistes de sous-titres texte a changé ; comparaison ASS impossible.");
            else for(var i=0;i<sourceText.Length;i++)
            {
                var sourceEvents=await ExtractAssEventsAsync(tools,original,sourceText[i],directory,ct);
                var outputEvents=await ExtractAssEventsAsync(tools,current,outputText[i],directory,ct);
                if(!sourceEvents.SequenceEqual(outputEvents,StringComparer.Ordinal))
                { issues.Add($"Les événements ASS de la piste de sous-titres {i+1} ont changé."); break; }
            }
        }

        if(Flag(config,"verifyDecoding"))
            await tools.CheckedAsync("ffmpeg",["-nostdin","-v","error","-xerror","-i",current,"-map","0:v?","-map","0:a?","-f","null","-"],directory,ct);

        return JsonSerializer.SerializeToElement(new { valid=issues.Count==0,issues });
    }

    private static IEnumerable<JsonElement> Streams(JsonElement metadata)
    {
        if(metadata.TryGetProperty("streams",out var streams)&&streams.ValueKind==JsonValueKind.Array)
            return streams.EnumerateArray().ToArray();
        return [];
    }
    private static string CodecType(JsonElement stream)=>stream.TryGetProperty("codec_type",out var type)?type.GetString()??"":"";
    private static string CodecName(JsonElement stream)=>stream.TryGetProperty("codec_name",out var codec)?codec.GetString()??"":"";
    private static bool IsAttachedPicture(JsonElement stream)=>stream.TryGetProperty("disposition",out var disposition)&&disposition.TryGetProperty("attached_pic",out var cover)&&cover.ValueKind==JsonValueKind.Number&&cover.GetInt32()!=0;
    private static int? MetadataInt(JsonElement source,string name)=>source.TryGetProperty(name,out var value)&&value.TryGetInt32(out var number)?number:null;
    private static double? MetadataNumber(JsonElement source,string parent,string name)
    {
        if(!source.TryGetProperty(parent,out var group)||!group.TryGetProperty(name,out var value)) return null;
        if(value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out var number)) return number;
        if(value.ValueKind==JsonValueKind.String&&double.TryParse(value.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var parsed)) return parsed;
        return null;
    }
    private static int? ChapterCount(JsonElement metadata)=>metadata.TryGetProperty("chapters",out var chapters)&&chapters.ValueKind==JsonValueKind.Array?chapters.GetArrayLength():null;
    private static string Tag(JsonElement stream,string name)=>stream.TryGetProperty("tags",out var tags)&&tags.TryGetProperty(name,out var value)?value.GetString()??"":"";
    private static bool Disposition(JsonElement stream,string name)=>stream.TryGetProperty("disposition",out var disposition)&&disposition.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.GetInt32()!=0;
    private static string AudioLayout(JsonElement stream)=>stream.TryGetProperty("channel_layout",out var layout)?layout.GetString()??"":"";
    private static string NormalizeLayout(string value)=>value.ToLowerInvariant().Replace("(side)","").Replace("sl","bl",StringComparison.Ordinal).Replace("sr","br",StringComparison.Ordinal);
    private static bool HasLfe(string value)=>value.Contains("lfe",StringComparison.OrdinalIgnoreCase)||System.Text.RegularExpressions.Regex.IsMatch(value,@"\.1(?:\(|$)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static string[] Csv(IReadOnlyDictionary<string,JsonElement> config,string key)=>Text(config,key).Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);
    private static bool IsTextSubtitle(JsonElement stream)=>new[]{"ass","ssa","subrip","srt","webvtt","mov_text","text"}.Contains(CodecName(stream),StringComparer.OrdinalIgnoreCase);
    private static string[] Attachments(JsonElement identify)=>identify.TryGetProperty("attachments",out var attachments)&&attachments.ValueKind==JsonValueKind.Array
        ?attachments.EnumerateArray().Select(item=>string.Join('|',AttachmentField(item,"file_name","name"),AttachmentField(item,"content_type","mime_type"),AttachmentField(item,"size"))).ToArray():[];
    private static string AttachmentField(JsonElement item,string primary,string? alternate=null)=>item.TryGetProperty(primary,out var value)?value.ToString():alternate is not null&&item.TryGetProperty(alternate,out value)?value.ToString():"";
    private static async Task<string[]> ExtractAssEventsAsync(ProcessToolRunner tools,string media,JsonElement subtitle,string directory,CancellationToken ct)
    {
        var index=MetadataInt(subtitle,"index")??throw new MediaToolException("index de piste de sous-titres",1);
        var file=Path.Combine(directory,"validate-"+Guid.NewGuid().ToString("N")+".ass");
        try
        {
            await tools.CheckedAsync("ffmpeg",["-nostdin","-y","-v","error","-i",media,"-map","0:"+index,"-c:s","ass","-f","ass",file],directory,ct);
            var events=new List<string>();
            foreach(var line in await File.ReadAllLinesAsync(file,ct))
            {
                if(!line.StartsWith("Dialogue:",StringComparison.OrdinalIgnoreCase)) continue;
                var fields=line.Split(',',10);
                if(fields.Length<10) throw new IOException("Événement ASS mal formé pendant la validation.");
                events.Add(fields[1]+"|"+fields[2]+"|"+fields[9]);
            }
            return events.ToArray();
        }
        finally { if(File.Exists(file)) File.Delete(file); }
    }

    public async Task<MediaActionOutcome> FfmpegPlanAsync(IReadOnlyList<RuntimePlanStep> plan,string current,string directory,string node,Guid workerId,CancellationToken ct,bool preserve=true,IReadOnlySet<int>? forceCpuFallbackSteps=null)
    {
        if (plan.Any(step => step.Configuration.ContainsKey("trackSelection")))
            plan = MediaTrackSelection.Expand(plan, await scanner.ScanAsync(current, ct));
        if (plan.Any(step => step.ComponentId is "base-resize-video" or "video-resize"))
        {
            var metadata = await scanner.ScanAsync(current, ct);
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
        if(preserve || !plan.Any(p=>p.ComponentId is "base-transcode-video" or "video-transcode" or "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize" or "base-resize-video" or "video-resize" or "base-auto-crop" or "video-auto-crop" or "base-deinterlace-video" or "video-deinterlace")) args.AddRange(["-map","0"]);
        else {
            if(plan.Any(p=>p.ComponentId is "base-transcode-video" or "video-transcode" or "base-resize-video" or "video-resize" or "base-auto-crop" or "video-auto-crop" or "base-deinterlace-video" or "video-deinterlace")) args.AddRange(["-map","0:v?"]);
            foreach(var track in plan.Where(p=>p.ComponentId is "base-transcode-audio" or "audio-transcode" or "base-normalize-audio" or "audio-normalize").Select(p=>Number(p.Configuration,"trackId")).Distinct()) args.AddRange(["-map","0:a:"+track]);
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
                    JsonElement? currentMetadata=null;
                    try { currentMetadata=await scanner.ScanAsync(current,ct); } catch { }
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
                case "base-resize-video" or "video-resize": videoFilters.Add(VideoResizePolicy.Filter(c)); if(!plan.Any(p=>p.ComponentId is "base-transcode-video" or "video-transcode")) args.AddRange(["-c:v","libx264"]); break;
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
            return await FfmpegPlanAsync(plan,current,directory,node,workerId,ct,preserve,hardwareFallbackSteps);
        }
        return new(output,[],null,resolutions);
    }

    private async Task<MediaActionOutcome> MatroskaAsync(string id,IReadOnlyDictionary<string,JsonElement> c,string current,string directory,string node,Func<string,Task<string>> extra,CancellationToken ct)
    {
        var file=Output(directory,node,".mkv");
        if(id is "base-extract-track" or "base-extract-attachments" or "base-extract-chapters" or "mkv-extract-track" or "mkv-extract-attachments" or "mkv-extract-chapters")
        {
            if(id is "base-extract-chapters" or "mkv-extract-chapters")
            {
                var simple=Text(c,"format","xml")!="xml"; var args=new List<string> { current,"chapters" }; if(simple) args.Add("--simple");
                file=Output(directory,node,simple?".txt":".xml"); var text=await tools.CheckedAsync("mkvextract",args,directory,ct); await File.WriteAllTextAsync(file,text,ct); return new(null,[file]);
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
                if(selected.Length==0) { await tools.CheckedAsync("mkvmerge",["-o",file+".next.mkv","--no-attachments",current],directory,ct); File.Move(file+".next.mkv",file,true); return new(file,[]); }
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
            if(ids.Length>0) foreach(var type in types) merge.AddRange(["--"+(type=="subtitles"?"subtitle":type)+"-tracks",((id is "base-remove-tracks" or "mkv-remove-tracks")?"!":"")+string.Join(',',ids)]);
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
            merge.AddRange([id is "base-add-audio-track" or "audio-add-track"?"--default-track-flag":"--forced-display-flag","0:"+(Flag(c,id is "base-add-audio-track" or "audio-add-track"?"defaultTrack":"forcedTrack")?"1":"0")]);
            merge.Add(await extra(Text(c,"sourceFile")));
        }
        await tools.CheckedAsync("mkvmerge",merge,directory,ct); return new(file,[]);
    }
}
