using RemoteDeskHub.Core.Abstractions;
using RemoteDeskHub.Shared;
using RemoteDeskHub.Shared.Protocol;

namespace RemoteDeskHub.UnitTests;

public sealed class ProtocolSerializationTests
{
    [Fact]
    public void OperationResult_RoundTrips()
    {
        var json = JsonDefaults.Serialize(OperationResult.Fail("nope"));
        var parsed = JsonDefaults.Deserialize<OperationResult>(json)!;

        Assert.False(parsed.Success);
        Assert.Equal("nope", parsed.Error);
    }

    [Fact]
    public void OperationResultOfT_RoundTripsValue()
    {
        var result = OperationResult<int>.Ok(42, "done");
        var parsed = JsonDefaults.Deserialize<OperationResult<int>>(JsonDefaults.Serialize(result))!;

        Assert.True(parsed.Success);
        Assert.Equal(42, parsed.Value);
        Assert.Equal("done", parsed.Message);
    }

    [Fact]
    public void Enums_SerializeAsCamelCaseStrings()
    {
        var json = JsonDefaults.Serialize(new CommandRequestDto
        {
            Command = "hostname",
            Shell = CommandShell.Cmd
        });

        Assert.Contains("\"shell\":\"cmd\"", json);
    }

    [Fact]
    public void FileEntryDto_RequiresCoreFields()
    {
        var entry = new FileEntryDto
        {
            Name = "readme.txt",
            FullPath = @"C:\Temp\readme.txt",
            Attributes = "Archive",
            SizeBytes = 120,
            ModifiedUtc = DateTimeOffset.UtcNow
        };

        var parsed = JsonDefaults.Deserialize<FileEntryDto>(JsonDefaults.Serialize(entry))!;

        Assert.Equal(entry.FullPath, parsed.FullPath);
        Assert.Equal(entry.ModifiedUtc.ToUnixTimeSeconds(), parsed.ModifiedUtc.ToUnixTimeSeconds());
    }

    [Fact]
    public void CommandResultDto_RoundTripsExitCodeAndOutput()
    {
        var result = new CommandResultDto
        {
            CorrelationId = Guid.NewGuid(),
            ExitCode = 0,
            StdOut = "hello",
            StdErr = string.Empty,
            DurationMs = 15
        };

        var parsed = JsonDefaults.Deserialize<CommandResultDto>(JsonDefaults.Serialize(result))!;

        Assert.Equal(0, parsed.ExitCode);
        Assert.Equal("hello", parsed.StdOut);
        Assert.Equal(result.CorrelationId, parsed.CorrelationId);
    }

    [Fact]
    public void TransferProgress_ComputesPercentageAndClamps()
    {
        Assert.Equal(50, new TransferProgress(50, 100, 10, "a").Percent);
        Assert.Equal(0, new TransferProgress(50, 0, 10, "a").Percent);
        Assert.Equal(100, new TransferProgress(200, 100, 10, "a").Percent);
    }

    [Fact]
    public void PagedResult_ExposesItemsAndTotal()
    {
        var page = new PagedResult<string>(["a", "b"], 42);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal(42, page.TotalCount);
    }
}
