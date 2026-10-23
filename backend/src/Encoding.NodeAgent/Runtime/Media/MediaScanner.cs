using System.Text.Json;

namespace Encoding.NodeAgent.Runtime.Media;

public sealed class MediaScanner(ProcessToolRunner tools)
{
    public async Task<JsonElement> ScanAsync(string path,CancellationToken ct)
    {
        var json=await tools.CheckedAsync("ffprobe",["-v","error","-show_format","-show_streams","-show_chapters","-of","json",path],Path.GetDirectoryName(path)!,ct);
        using var document=JsonDocument.Parse(json);
        if(!document.RootElement.TryGetProperty("streams",out var streams) || streams.GetArrayLength()==0) throw new IOException("Aucune piste média valide détectée.");
        var sanitized=System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!;
        if(sanitized["format"] is System.Text.Json.Nodes.JsonObject format) format["filename"]=Path.GetFileName(path);
        return JsonSerializer.SerializeToElement(sanitized);
    }
    public async Task<JsonElement> ValidateAsync(string candidate,string mediaType,JsonElement? before,CancellationToken ct)
    {
        if(!File.Exists(candidate) || new FileInfo(candidate).Length==0) throw new IOException("Le résultat média est vide.");
        var metadata=await ScanAsync(candidate,ct);
        var expected=mediaType=="music"?"audio":mediaType=="image"?"video":mediaType=="subtitles"?"subtitle":"video";
        if(!metadata.GetProperty("streams").EnumerateArray().Any(stream=>stream.TryGetProperty("codec_type",out var type) && type.GetString()==expected)) throw new IOException("Le résultat ne contient pas le média attendu.");
        if(mediaType is "video" or "music")
        {
            static double? Duration(JsonElement? value)=>value is { } json && json.TryGetProperty("format",out var format) && format.TryGetProperty("duration",out var duration)
                && double.TryParse(duration.GetString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var number)?number:null;
            var a=Duration(before); var b=Duration(metadata);
            if(a>0 && (b is null || Math.Abs(a.Value-b.Value)>Math.Max(2,a.Value*0.02))) throw new IOException("La durée du résultat est incompatible avec la source.");
        }
        return metadata;
    }
    /// <summary>Décodage complet des flux audio/vidéo (ADR 0007) : exigé une fois, juste avant la publication d'un résultat.</summary>
    public async Task DecodeAsync(string candidate,string mediaType,CancellationToken ct)
    {
        if(mediaType is not ("video" or "music")) return;
        var result=await tools.RunAsync(ProcessToolRunner.Find("ffmpeg") ?? throw new IOException("Outil indisponible : ffmpeg."),
            ["-nostdin","-v","error","-xerror","-i",candidate,"-map","0:v?","-map","0:a?","-f","null","-"],Path.GetDirectoryName(candidate)!,TimeSpan.FromHours(6),ct);
        // ffmpeg 6.x sort en 0 sur une image corrompue ou un conteneur abîmé, même avec -xerror : avec « -v error », toute ligne d'erreur sur stderr est un échec.
        if(result.ExitCode!=0 || !string.IsNullOrWhiteSpace(result.Error))
        {
            var detail=result.Error.Trim(); if(detail.Length>400) detail=detail[..400];
            throw new MediaToolException("ffmpeg (décodage de contrôle avant publication)",result.ExitCode,detail);
        }
    }
}
