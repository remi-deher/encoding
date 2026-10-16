using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Net;

namespace Encoding.Api.Infrastructure.PeerCluster;

public sealed record PeerIdentity(Guid PeerId, Guid ClusterId, X509Certificate2 Certificate)
{
    public string Thumbprint => Fingerprint(Certificate);
    public string CertificatePem => Certificate.ExportCertificatePem();

    public static string NormalizeThumbprint(string value) => new(value.Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    public static string Fingerprint(X509Certificate2 certificate) =>
        NormalizeThumbprint(certificate.GetCertHashString(HashAlgorithmName.SHA256));
}

/// <summary>Persists the server identity and private key on this server's local data volume.</summary>
public sealed class PeerIdentityStore : IDisposable
{
    private const string CertificateFileName = "peer-identity.pfx";
    private const string MetadataFileName = "peer-identity.json";
    private const string IncarnationFileName = "peer-incarnation.txt";
    private readonly string directory;
    private readonly FileStream processLock;

    private PeerIdentityStore(string directory, PeerIdentity identity, FileStream processLock)
    {
        this.directory = directory;
        Identity = identity;
        this.processLock = processLock;
    }

    public PeerIdentity Identity { get; }

    public static PeerIdentityStore Open(string dataDirectory, Guid clusterId, string certificateHost = "localhost", string? pfxPassword = null)
    {
        Directory.CreateDirectory(dataDirectory);
        var lockFile = Path.Combine(dataDirectory, "peer-identity.lock");
        FileStream processLock;
        try { processLock = new FileStream(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new InvalidOperationException("Un autre processus utilise déjà l’identité de pair de ce dossier.", exception); }

        try
        {
            var certificatePath = Path.Combine(dataDirectory, CertificateFileName);
            var metadataPath = Path.Combine(dataDirectory, MetadataFileName);
            PeerIdentity identity;
            if (File.Exists(certificatePath) && File.Exists(metadataPath))
            {
                var metadata = JsonSerializer.Deserialize<IdentityMetadata>(File.ReadAllText(metadataPath))
                    ?? throw new InvalidOperationException("Les métadonnées de l’identité de pair sont illisibles.");
                if (metadata.ClusterId != clusterId) throw new InvalidOperationException("L’identité de pair persistée appartient à un autre PeerCluster:ClusterId.");
                var certificate = X509CertificateLoader.LoadPkcs12FromFile(certificatePath, pfxPassword,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                identity = new PeerIdentity(metadata.PeerId, metadata.ClusterId, certificate);
                ValidateOwnCertificate(identity);
            }
            else
            {
                if (File.Exists(certificatePath) || File.Exists(metadataPath))
                    throw new InvalidOperationException("Les fichiers de l’identité de pair sont incomplets. Restaurer les deux depuis la même sauvegarde.");
                identity = CreateIdentity(clusterId, certificateHost);
                File.WriteAllBytes(certificatePath, identity.Certificate.Export(X509ContentType.Pkcs12, pfxPassword));
                SetPrivateFilePermissions(certificatePath);
                WriteMetadata(metadataPath, new IdentityMetadata(identity.PeerId, clusterId));
            }
            SetPrivateFilePermissions(lockFile);
            return new PeerIdentityStore(dataDirectory, identity, processLock);
        }
        catch
        {
            processLock.Dispose();
            throw;
        }
    }

    public long StartNewIncarnation()
    {
        var path = Path.Combine(directory, IncarnationFileName);
        long current = 0;
        if (File.Exists(path) && !long.TryParse(File.ReadAllText(path), out current))
            throw new InvalidOperationException("Le compteur d’incarnation du pair est illisible.");
        if (current == long.MaxValue) throw new InvalidOperationException("Le compteur d’incarnation du pair a atteint sa limite.");
        var next = Math.Max(current + 1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, next.ToString(System.Globalization.CultureInfo.InvariantCulture), System.Text.Encoding.ASCII);
        SetPrivateFilePermissions(temporary);
        File.Move(temporary, path, overwrite: true);
        SetPrivateFilePermissions(path);
        return next;
    }

    public byte[] Sign(ReadOnlySpan<byte> payload)
    {
        using var privateKey = Identity.Certificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("La clé privée ECDSA du pair est absente.");
        return privateKey.SignData(payload, HashAlgorithmName.SHA256);
    }

    public PeerIdentity PrepareRotation(string host, string password)
    {
        var next = CreateIdentity(Identity.ClusterId, host, Identity.PeerId);
        var path = Path.Combine(directory, "peer-identity.next.pfx");
        if (File.Exists(path)) { next.Certificate.Dispose(); throw new InvalidOperationException("Une rotation est déjà préparée."); }
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            SetPrivateFilePermissions(path);
            file.Write(next.Certificate.Export(X509ContentType.Pkcs12, password));
            file.Flush(flushToDisk: true);
            return next;
        }
        catch (IOException exception) when (File.Exists(path))
        {
            next.Certificate.Dispose();
            throw new InvalidOperationException("La préparation du certificat n’a pas pu être confirmée ; vérifiez le fichier de rotation local.", exception);
        }
        catch { next.Certificate.Dispose(); throw; }
    }

    public void ActivateRotation(string password, string approvedFingerprint)
    {
        var nextPath = Path.Combine(directory, "peer-identity.next.pfx");
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(nextPath, password, X509KeyStorageFlags.EphemeralKeySet);
        var next = new PeerIdentity(Identity.PeerId, Identity.ClusterId, certificate);
        ValidateOwnCertificate(next);
        if (next.Thumbprint != PeerIdentity.NormalizeThumbprint(approvedFingerprint))
            throw new InvalidOperationException("L’empreinte approuvée ne correspond pas au certificat préparé.");
        var current = Path.Combine(directory, CertificateFileName);
        File.Copy(current, Path.Combine(directory, "peer-identity.previous.pfx"), overwrite: true);
        SetPrivateFilePermissions(Path.Combine(directory, "peer-identity.previous.pfx"));
        File.Move(nextPath, current, overwrite: true);
        SetPrivateFilePermissions(current);
    }

    private static PeerIdentity CreateIdentity(Guid clusterId, string certificateHost, Guid? existingPeerId = null)
    {
        var peerId = existingPeerId ?? Guid.NewGuid();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=EncodingPeer-{clusterId:N}-{peerId:N}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var usages = new OidCollection
        {
            new("1.3.6.1.5.5.7.3.1"), // TLS server authentication
            new("1.3.6.1.5.5.7.3.2")  // TLS client authentication
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(certificateHost, out var address)) subjectAlternativeNames.AddIpAddress(address);
        else subjectAlternativeNames.AddDnsName(certificateHost);
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        return new PeerIdentity(peerId, clusterId, certificate);
    }

    private static void ValidateOwnCertificate(PeerIdentity identity)
    {
        using var privateKey = identity.Certificate.GetECDsaPrivateKey();
        if (!identity.Certificate.HasPrivateKey || privateKey is null
            || !CertificateSubjectMatches(identity.Certificate, identity.ClusterId, identity.PeerId))
            throw new InvalidOperationException("Le certificat de l’identité de pair ne correspond pas à ses métadonnées.");
    }

    internal static bool CertificateSubjectMatches(X509Certificate2 certificate, Guid clusterId, Guid peerId) =>
        certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false)
            .Equals($"EncodingPeer-{clusterId:N}-{peerId:N}", StringComparison.OrdinalIgnoreCase);

    internal static void SetPrivateFilePermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void WriteMetadata(string path, IdentityMetadata metadata)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(metadata));
        SetPrivateFilePermissions(temporary);
        File.Move(temporary, path, overwrite: true);
        SetPrivateFilePermissions(path);
    }

    public void Dispose()
    {
        Identity.Certificate.Dispose();
        processLock.Dispose();
    }

    private sealed record IdentityMetadata(Guid PeerId, Guid ClusterId);
}
