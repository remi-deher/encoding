using Encoding.NodeAgent.Runtime;
using Encoding.NodeAgent.Runtime.Media;
using Xunit;

namespace Encoding.Runtime.Tests;

/// <summary>ADR 0007 : la validation d'une étape est légère (sans décodage) ; le décodage complet est une étape distincte, avant publication.</summary>
public sealed class MediaDecodeValidationTests
{
    [MediaToolsFact]
    public async Task StepValidationDoesNotDecodeButThePublicationDecodeDetectsACorruptedStream()
    {
        var directory = Path.Combine(Path.GetTempPath(), "encoding-decode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tools = new ProcessToolRunner();
            var scanner = new MediaScanner(tools);
            var good = Path.Combine(directory, "good.h264");
            await tools.CheckedAsync("ffmpeg", ["-v", "error", "-y", "-f", "lavfi", "-i", "testsrc=duration=4:size=128x72:rate=25",
                "-c:v", "libx264", "-g", "5", "-f", "h264", good], directory, CancellationToken.None);
            var before = await scanner.ScanAsync(good, CancellationToken.None);
            await scanner.ValidateAsync(good, "video", before, CancellationToken.None);
            await scanner.DecodeAsync(good, "video", CancellationToken.None);

            // Des octets du flux sont altérés au milieu du fichier : ffprobe lit encore le fichier, le décodage non (ffmpeg sort en 0 : c'est stderr qui le dit).
            var bytes = await File.ReadAllBytesAsync(good);
            var corrupted = Path.Combine(directory, "corrupted.h264");
            for (var index = bytes.Length / 2; index < bytes.Length / 2 + 400; index++) bytes[index] ^= 0xA5;
            await File.WriteAllBytesAsync(corrupted, bytes);

            await scanner.ValidateAsync(corrupted, "video", before, CancellationToken.None);
            var failure = await Assert.ThrowsAsync<MediaToolException>(() => scanner.DecodeAsync(corrupted, "video", CancellationToken.None));
            Assert.Contains("décodage de contrôle", failure.Message);
            await scanner.DecodeAsync(corrupted, "subtitles", CancellationToken.None);
        }
        finally { Directory.Delete(directory, true); }
    }
}
