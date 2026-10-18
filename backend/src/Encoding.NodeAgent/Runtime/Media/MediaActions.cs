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
        if(id is "base-transcode-video" or "base-transcode-audio" or "base-normalize-audio" or "base-resize-video" or "base-remux-media" or "base-auto-crop" or "base-deinterlace-video")
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
    public async Task<MediaActionOutcome> AddFontsAsync(IReadOnlyDictionary<string,JsonElement> c,string current,string directory,string node,CancellationToken ct)
    {
        if(Path.GetExtension(current)!=".mkv") return new(null,[]);
        var root=Path.GetFullPath(Environment.GetEnvironmentVariable("ENCODING_FONT_DIR") is { Length: >0 } configured?configured:"/app/common/Fonts");
        var set=Text(c,"fontSet","Crunchyroll");
        if(set.Length==0||!System.Text.RegularExpressions.Regex.IsMatch(set,@"^[A-Za-z0-9][A-Za-z0-9 ._-]*$")||set.Contains("..",StringComparison.Ordinal)) throw new SubtitleException("Nom de jeu de polices invalide.");
        var folder=Path.GetFullPath(Path.Combine(root,set));
        if(!folder.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.Ordinal)||!Directory.Exists(folder)) throw new SubtitleException($"Jeu de polices « {set} » introuvable sur ce nœud.");
        var extensions = new[] { ".ttf", ".otf", ".ttc" };
        var names=Text(c,"fonts").Split([',','\n',';'],StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).ToList();
        if(names.Count==0) names=Directory.EnumerateFiles(folder).Select(Path.GetFileName).Where(name=>extensions.Contains(Path.GetExtension(name)!.ToLowerInvariant())).Order(StringComparer.Ordinal).ToList()!;
        if(names.Count==0) throw new SubtitleException($"Le jeu de polices « {set} » ne contient aucune police.");
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

    public async Task<MediaActionOutcome> FfmpegPlanAsync(IReadOnlyList<RuntimePlanStep> plan,string current,string directory,string node,Guid workerId,CancellationToken ct,bool preserve=true)
    {
        var output=Output(directory,node,Path.GetExtension(current) is ".mp4" or ".mkv" or ".mka" or ".flac"?Path.GetExtension(current):".mkv");
        var input=new List<string> { "-nostdin","-y","-v","error" }; var args=new List<string> { "-c","copy" }; var videoFilters=new List<string>(); var hardwareMode="cpu";
        var resolutions=new List<ResolvedVideoEncoding>();
        if(preserve || !plan.Any(p=>p.ComponentId is "base-transcode-video" or "base-transcode-audio" or "base-normalize-audio" or "base-resize-video" or "base-auto-crop" or "base-deinterlace-video")) args.AddRange(["-map","0"]);
        else {
            if(plan.Any(p=>p.ComponentId is "base-transcode-video" or "base-resize-video" or "base-auto-crop" or "base-deinterlace-video")) args.AddRange(["-map","0:v?"]);
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
                        var quality=MediaCodecPolicy.ResolveQuality(encoder,qualityTier,sourceWidth,sourceHeight,target.Quality);
                        hardwareMode=encoder.Acceleration;
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
                        var hardware=Text(c,"hardwareAcceleration","cpu"); hardwareMode=hardware;
                        var available=ffmpeg.Encoders??[];
                        var encoder=MediaCodecPolicy.VideoEncoder(codec,hardware,available)??throw new MediaToolException("encodeur vidéo",1);
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
                case "base-transcode-audio":
                    var index=Number(c,"trackId"); var audio=Text(c,"audioCodec","aac"); args.AddRange(["-c:a:"+index,audio=="opus"?"libopus":audio]);
                    if(audio!="flac") args.AddRange(["-b:a:"+index,Number(c,"bitrateKbps",192)+"k"]);
                    if(Path.GetExtension(current)==".flac" && audio!="flac" || Path.GetExtension(current)==".mp4" && audio=="flac") output=Output(directory,node,".mkv");
                    if(Text(c,"channels","source")!="source") args.AddRange(["-ac:a:"+index,Text(c,"channels")]);
                    // Disposition déclarée à l’entrée quand la source n’en a pas (déduite, 0055) et disposition imposée à la sortie pour garder le LFE.
                    if(Text(c,"inputLayout")!="") input.AddRange(["-channel_layout:a:"+index,Text(c,"inputLayout")]);
                    if(Text(c,"outputLayout")!="") args.AddRange(["-channel_layout:a:"+index,Text(c,"outputLayout")]);
                    if(Number(c,"sampleRate")!="0") args.AddRange(["-ar:a:"+index,Number(c,"sampleRate")]); break;
                case "base-normalize-audio": if(Path.GetExtension(current)==".flac") output=Output(directory,node,".mka"); args.AddRange(["-filter:a:"+Number(c,"trackId"),"loudnorm=I="+Number(c,"targetLoudness",-16)+":TP="+Number(c,"truePeak",-1),"-c:a:"+Number(c,"trackId"),"aac"]); break;
                case "base-resize-video": videoFilters.Add("scale="+Number(c,"width",1920)+":"+Number(c,"height",1080)+":force_original_aspect_ratio=decrease"); if(!plan.Any(p=>p.ComponentId=="base-transcode-video")) args.AddRange(["-c:v","libx264"]); break;
                case "base-auto-crop":
                    var cropF=Text(c,"cropFilter");
                    if(!string.IsNullOrEmpty(cropF)) videoFilters.Add(cropF);
                    if(!plan.Any(p=>p.ComponentId=="base-transcode-video")) args.AddRange(["-c:v","libx264"]);
                    break;
                case "base-deinterlace-video":
                    var deintAlgo=Text(c,"algorithm","bwdif");
                    if(!string.IsNullOrEmpty(deintAlgo)) videoFilters.Add(deintAlgo);
                    if(!plan.Any(p=>p.ComponentId=="base-transcode-video")) args.AddRange(["-c:v","libx264"]);
                    break;
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
                args.AddRange(["--edit","track:="+track.GetProperty("properties").GetProperty("uid").GetUInt64()]);
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
