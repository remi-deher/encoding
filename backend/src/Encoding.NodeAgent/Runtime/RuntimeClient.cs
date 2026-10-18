using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using Encoding.Contracts.Executions;
using Encoding.NodeAgent.Services;

namespace Encoding.NodeAgent.Runtime;

public sealed class RuntimeClient : IDisposable
{
    private readonly HttpClient http=new() { Timeout=Timeout.InfiniteTimeSpan };
    private readonly WorkerIdentityState identity;
    public RuntimeClient(WorkerIdentityState identity)
    { this.identity=identity; http.BaseAddress=new Uri(identity.ApiBaseUrl!); http.DefaultRequestHeaders.Add("X-Node-Token",identity.NodeToken); }
    public Guid NodeId=>identity.Id;
    private string Route(string path)=>$"api/v1/runtime/nodes/{identity.Id:D}/{path}";
    public async Task<T?> PostAsync<T>(string path,object body,CancellationToken ct)
    {
        using var response=await http.PostAsJsonAsync(Route(path),body,AgentJson.Options,ct);
        if(!response.IsSuccessStatusCode)
        {
            var content=await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Content: {content}", null, response.StatusCode);
        }
        return response.StatusCode==HttpStatusCode.NoContent?default:await response.Content.ReadFromJsonAsync<T>(AgentJson.Options,ct);
    }
    public async Task<T> GetAsync<T>(string path,CancellationToken ct)
    {
        using var response=await http.GetAsync(Route(path),ct);
        if(!response.IsSuccessStatusCode)
        {
            var content=await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Content: {content}", null, response.StatusCode);
        }
        return (await response.Content.ReadFromJsonAsync<T>(AgentJson.Options,ct))!;
    }
    public async Task<ExecutionResponse> ExecutionAsync(Guid id,CancellationToken ct)=>(await http.GetFromJsonAsync<ExecutionResponse>($"api/v1/executions/{id:D}",AgentJson.Options,ct))!;
    public async Task PutAsync(Guid id,long epoch,string file,string sha,bool original,CancellationToken ct,string? inputPath=null)
    {
        await using var stream=File.OpenRead(file); using var content=new StreamContent(stream);
        using var response=await http.PutAsync(Route($"executions/{id:D}/dav/{sha}?epoch={epoch}&original={original.ToString().ToLowerInvariant()}{(inputPath is null?string.Empty:"&inputPath="+Uri.EscapeDataString(inputPath))}"),content,ct);
        if(!response.IsSuccessStatusCode)
        {
            var err=await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Content: {err}", null, response.StatusCode);
        }
    }
    public async Task DownloadAsync(Guid id,long epoch,string sha,string target,CancellationToken ct)
    {
        if(File.Exists(target) && await SafeMediaPaths.DigestAsync(target,ct)==sha) return;
        var partial=target+".partial"; var offset=File.Exists(partial)?new FileInfo(partial).Length:0;
        using var request=new HttpRequestMessage(HttpMethod.Get,Route($"executions/{id:D}/dav/{sha}?epoch={epoch}"));
        if(offset>0) request.Headers.Range=new RangeHeaderValue(offset,null);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        if(response.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(partial); throw new IOException("Le téléchargement partiel doit être recommencé."); }
        if(!response.IsSuccessStatusCode)
        {
            var content=await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Content: {content}", null, response.StatusCode);
        }
        await using(var file=new FileStream(partial,response.StatusCode==HttpStatusCode.PartialContent?FileMode.Append:FileMode.Create,FileAccess.Write,FileShare.None,81920,FileOptions.Asynchronous))
        { await response.Content.CopyToAsync(file,ct); await file.FlushAsync(ct); file.Flush(true); }
        if(await SafeMediaPaths.DigestAsync(partial,ct)!=sha) { File.Delete(partial); throw new IOException("Le téléchargement est incomplet ou corrompu."); }
        File.Move(partial,target,true);
    }
    public void Dispose()=>http.Dispose();
}
