using Missum.App.Services;
using Missum.Core.Coding;
using Missum.Core.Models;

namespace Missum.Tests;

/// <summary>Findings from the MissumDivided live run: chat image names and locked editor logs.</summary>
public sealed class MediaAnalysisDisplayTests
{
    private static ChatArtifact Original(string stepId, string uploadId, string fileName = "original.png") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "artifact-1", fileName, "image/png", new string('a', 64), 10,
            "media", DateTimeOffset.UtcNow, new Dictionary<string, string> { ["role"] = "original", ["sourceUploadId"] = uploadId }, stepId);

    [Fact]
    public void AnalyzedOriginalShowsTheFileNameImageInputLoaded()
    {
        var steps = new List<AssistantToolStep>
        {
            new("load", "image.input", "completed",
                InputJson: "{\"operation\":\"file\",\"path\":\"Saved/Screenshots/WindowsEditor/HighresScreenshot00003.png\"}",
                OutputJson: "{\"uploadId\":\"upload-3\",\"sourcePath\":\"C:\\\\P\\\\Saved\\\\Screenshots\\\\WindowsEditor\\\\HighresScreenshot00003.png\"}"),
            new("analyze", "media.analyze", "completed", InputJson: "{\"uploadId\":\"upload-3\"}",
                OutputJson: "{\"kind\":\"image\",\"primaryUpload\":{\"uploadId\":\"upload-3\",\"fileName\":\"HighresScreenshot00003.png\"}}"),
        };
        Assert.Equal("HighresScreenshot00003.png", AssistantArtifactOriginalResolver.DisplayFileName(Original("analyze", "upload-3"), steps));
    }

    [Fact]
    public void WithoutPrimaryUploadTheLoadingStepNamesTheImage()
    {
        var steps = new List<AssistantToolStep>
        {
            new("load", "image.input", "completed", InputJson: "{\"operation\":\"file\",\"path\":\"shots/view.png\"}",
                OutputJson: "{\"uploadId\":\"upload-7\"}"),
            new("analyze", "media.inspect", "completed", InputJson: "{\"uploadId\":\"upload-7\"}", OutputJson: "{\"kind\":\"image\"}"),
        };
        Assert.Equal("view.png", AssistantArtifactOriginalResolver.DisplayFileName(Original("analyze", "upload-7"), steps));
    }

    [Fact]
    public void OtherArtifactsKeepTheirOwnName()
    {
        var steps = new List<AssistantToolStep>
        {
            new("analyze", "media.analyze", "completed", OutputJson: "{\"primaryUpload\":{\"fileName\":\"shot.png\"}}"),
        };
        var named = Original("analyze", "upload-1", "plan.png");
        Assert.Equal("plan.png", AssistantArtifactOriginalResolver.DisplayFileName(named, steps));
        var unknown = Original("missing", "upload-1");
        Assert.Equal("original.png", AssistantArtifactOriginalResolver.DisplayFileName(unknown, steps));
    }

    [Fact]
    public async Task FileHeldOpenForWritingIsStillReadable()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("missum-shared-read").FullName, "Editor.log");
        try
        {
            await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await writer.WriteAsync("LogInit: started\n"u8.ToArray());
            await writer.FlushAsync();
            var bytes = await LocalCodingToolExecutor.ReadSharedAsync(path, CancellationToken.None);
            Assert.Equal("LogInit: started\n", System.Text.Encoding.UTF8.GetString(bytes));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }
}
