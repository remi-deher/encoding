using Microsoft.AspNetCore.Server.Kestrel.Https;
using Encoding.Api.Nodes;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Http;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace Encoding.Api.Infrastructure.PeerCluster;

public static class PeerClusterExtensions
{
    public static PeerClusterOptions AddPeerCluster(this WebApplicationBuilder builder)
    {
        var options = PeerClusterOptions.From(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<PeerPresenceRegistry>();
        if (!options.Enabled) return options;

        var identity = PeerIdentityStore.Open(options.DataDirectory, options.ClusterId!.Value,
            options.PublicEndpoint!.Host, options.IdentityPassword);
        var trust = new PeerTrustStore(options, identity.Identity);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(trust);
        builder.Services.AddSingleton<PeerConsensusService>();
        builder.Services.AddSingleton<IPeerAssignmentGate, PeerAssignmentGate>();
        builder.Services.AddHostedService<PeerPresenceGossipService>();
        builder.Services.AddHealthChecks().AddCheck<PeerClusterQuorumHealthCheck>("peer-cluster-quorum");
        var raftDirectory = Path.Combine(options.DataDirectory, "raft");
        Directory.CreateDirectory(raftDirectory);
        var membershipPath = Path.Combine(raftDirectory, "membership");
        builder.Services.UsePersistentConfigurationStorage(membershipPath);
        builder.Services.UseStateMachine<PeerStateMachine>(new WriteAheadLog.Options
        {
            Location = Path.Combine(raftDirectory, "wal"),
            FlushOnCommit = true
        });
        builder.Services.AddHttpClient("raftClient")
            .ConfigurePrimaryHttpMessageHandler(() =>
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false };
                handler.ClientCertificates.Add(identity.Identity.Certificate);
                handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    using var presented = X509CertificateLoader.LoadCertificate(certificate.RawData);
                    return trust.FindByCertificate(presented) is not null;
                };
                return handler;
            });
        builder.JoinCluster((configuration, _, _) =>
        {
            configuration.PublicEndPoint = options.PublicEndpoint!;
            configuration.ClientHandlerName = "raftClient";
            // Only the explicit bootstrap node creates an initial one-voter configuration. Fresh joiners
            // remain outside Raft until the leader catches them up and commits their membership.
            configuration.ColdStart = options.Bootstrap;
            configuration.LowerElectionTimeout = 1_500;
            configuration.UpperElectionTimeout = 3_000;
            configuration.IsLeaderLeaseEnabled = true;
        });
        var apiUrls = builder.Configuration[WebHostDefaults.ServerUrlsKey]?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? ParseHttpPorts(builder.Configuration[WebHostDefaults.HttpPortsKey] ?? builder.Configuration["ASPNETCORE_HTTP_PORTS"]);
        if (apiUrls.Length == 0) apiUrls = ["http://0.0.0.0:8080"];
        builder.WebHost.UseUrls(string.Join(';', apiUrls.Append($"https://0.0.0.0:{options.ListenPort}")));
        builder.WebHost.ConfigureKestrel(server => server.ConfigureEndpointDefaults(listen =>
        {
            if (listen.EndPoint is not System.Net.IPEndPoint endpoint || endpoint.Port != options.ListenPort) return;
            listen.UseHttps(https =>
            {
                https.ServerCertificate = identity.Identity.Certificate;
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (certificate, _, _) => trust.IsTrustedClientCertificate(certificate);
            });
        }));
        return options;
    }

    private static string[] ParseHttpPorts(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(port => $"http://0.0.0.0:{port}").ToArray();

    public static void MapPeerClusterEndpoints(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<PeerClusterOptions>();
        if (!options.Enabled) return;

        var identityStore = app.Services.GetRequiredService<PeerIdentityStore>();
        var trustStore = app.Services.GetRequiredService<PeerTrustStore>();
        var registry = app.Services.GetRequiredService<PeerPresenceRegistry>();
        var consensus = app.Services.GetRequiredService<PeerConsensusService>();
        var group = app.MapGroup("/api/v1/peer-cluster").WithTags("Peer cluster");

        // Public certificate retrieval is only a transport step. Administrators still pin it explicitly on each server.
        group.MapGet("/identity", () => Results.Ok(new
        {
            identityStore.Identity.ClusterId,
            identityStore.Identity.PeerId,
            identityStore.Identity.Thumbprint,
            identityStore.Identity.CertificatePem,
            Endpoint = options.PublicEndpoint
        })).AllowAnonymous().WithName("GetPeerIdentity");

        group.MapGet("", async (NodeRepository nodes, NodePresenceOptions nodeOptions, CancellationToken cancellationToken) =>
        {
            var local = await nodes.ListAsync(nodeOptions.OfflineAfter, cancellationToken);
            return Results.Ok(new PeerClusterView(identityStore.Identity.ClusterId, identityStore.Identity.PeerId,
                identityStore.Identity.Thumbprint, identityStore.Identity.CertificatePem,
                registry.BuildPeerViews(trustStore, options.OfflineAfter),
                registry.MergeNodes(local, nodeOptions.OfflineAfter), true));
        }).RequireAuthorization(Encoding.Api.Auth.AuthExtensions.AdministratorPolicy).WithName("GetPeerClusterView");

        group.MapGet("/consensus", () => Results.Ok(consensus.GetStatus()))
            .RequireAuthorization(Encoding.Api.Auth.AuthExtensions.AdministratorPolicy).WithName("GetPeerConsensusStatus");

        group.MapPost("/peers", async (TrustPeerRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                var status = consensus.GetStatus();
                var reactivating = consensus.IsPeerRevoked(request.PeerId);
                if (reactivating && !status.IsLeader)
                    return Results.Conflict(new { title = "La réactivation doit être demandée au chef Raft courant.", status.LeaderEndpoint });
                var peer = await trustStore.TrustAsync(request, cancellationToken);
                if (reactivating) await consensus.SetPeerRevokedAsync(request.PeerId, false, cancellationToken);
                return Results.Created($"/api/v1/peer-cluster/peers/{peer.PeerId:D}", new
                {
                    peer.PeerId,
                    peer.ClusterId,
                    peer.Endpoint,
                    peer.Thumbprint
                });
            }
            catch (ArgumentException exception) { return Results.BadRequest(new { title = exception.Message }); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                app.Logger.LogWarning(exception, "Peer trust or reactivation failed for peer {PeerId}.", request.PeerId);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Le quorum n’a pas confirmé la mise à jour de confiance du pair.");
            }
        }).RequireAuthorization(Encoding.Api.Auth.AuthExtensions.AdministratorPolicy).WithName("TrustPeer");

        group.MapDelete("/peers/{peerId:guid}", async (Guid peerId, CancellationToken cancellationToken) =>
        {
            var peer = trustStore.Find(peerId);
            if (peer is null) return Results.NotFound();
            var status = consensus.GetStatus();
            if (!status.IsLeader) return Results.Conflict(new { title = "La révocation doit être demandée au chef Raft courant.", status.LeaderEndpoint });
            try
            {
                if (consensus.IsMember(peer.Endpoint) && !await consensus.RemoveMemberAsync(peer.Endpoint, cancellationToken))
                    return Results.Conflict(new { title = "Le quorum n’a pas validé le retrait du membre." });
                await consensus.SetPeerRevokedAsync(peerId, true, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                app.Logger.LogWarning(exception, "Raft membership removal or revocation failed for peer {PeerId}.", peerId);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Le quorum Raft n’a pas validé le retrait ou la révocation du membre.");
            }
            var revoked = await trustStore.RevokeAsync(peerId, cancellationToken);
            if (revoked) registry.Remove(peerId);
            return revoked ? Results.NoContent() : Results.NotFound();
        })
            .RequireAuthorization(Encoding.Api.Auth.AuthExtensions.AdministratorPolicy).WithName("RevokePeer");

        group.MapPost("/peers/{peerId:guid}/membership", async (Guid peerId, CancellationToken cancellationToken) =>
        {
            var peer = trustStore.Find(peerId);
            if (peer is null) return Results.NotFound(new { title = "Le certificat du pair doit être approuvé sur les deux serveurs avant son ajout." });
            var status = consensus.GetStatus();
            if (!status.IsLeader) return Results.Conflict(new { title = "L’ajout doit être demandé au chef Raft courant.", status.LeaderEndpoint });
            try
            {
                return await consensus.AddMemberAsync(peer.Endpoint, cancellationToken)
                    ? Results.Accepted(value: new { peer.PeerId, peer.Endpoint })
                    : Results.Conflict(new { title = "Le pair n’a pas rejoint la membership Raft." });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                app.Logger.LogWarning(exception, "Raft membership addition failed for peer {PeerId} at {Endpoint}.", peerId, peer.Endpoint);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Le quorum Raft n’a pas validé l’ajout du membre.");
            }
        }).RequireAuthorization(Encoding.Api.Auth.AuthExtensions.AdministratorPolicy).WithName("AddPeerMembership");

        group.MapPost("/presence", async (PeerPresenceBatch batch, HttpContext context) =>
        {
            var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
            if (!trustStore.IsTrustedClientCertificate(certificate)) return Results.Unauthorized();
            if (batch.Records.Count > 128) return Results.BadRequest(new { title = "Le lot de présence dépasse la limite de 128 enregistrements." });
            var accepted = 0;
            foreach (var record in batch.Records)
            {
                var origin = trustStore.Find(record.PeerId);
                if (origin is not null && registry.Merge(record, identityStore.Identity.ClusterId, origin)) accepted++;
            }
            return Results.Accepted(value: new { accepted });
        }).AllowAnonymous().WithName("ReceivePeerPresence");
    }
}
