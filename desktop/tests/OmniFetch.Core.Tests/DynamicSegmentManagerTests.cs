using OmniFetch.Core.Common;
using OmniFetch.Core.Segmentation;
using Xunit;

namespace OmniFetch.Core.Tests;

public class DynamicSegmentManagerTests
{
    [Fact]
    public void InitializeNewJob_PartitionsFileEvenlyAcrossSegments()
    {
        // Arrange
        var manager = new DynamicSegmentManager();
        long totalBytes = 100_000_000; // 100 MB
        int connections = 4;
        var jobId = Guid.NewGuid();

        // Act
        manager.InitializeNewJob(jobId, totalBytes, connections);
        var segments = manager.GetSegments();

        // Assert
        Assert.Equal(connections, segments.Count);
        Assert.Equal(0, segments[0].StartByte);
        Assert.Equal(24_999_999, segments[0].EndByte);

        Assert.Equal(25_000_000, segments[1].StartByte);
        Assert.Equal(49_999_999, segments[1].EndByte);

        Assert.Equal(50_000_000, segments[2].StartByte);
        Assert.Equal(74_999_999, segments[2].EndByte);

        Assert.Equal(75_000_000, segments[3].StartByte);
        Assert.Equal(99_999_999, segments[3].EndByte);

        // Verify total byte coverage with zero gaps
        long coveredBytes = 0;
        foreach (var seg in segments)
        {
            coveredBytes += (seg.EndByte - seg.StartByte + 1);
        }
        Assert.Equal(totalBytes, coveredBytes);
    }

    [Fact]
    public void TryBisectLargestSegment_SelectsLargestRemainingWorkload_AndBisectsCorrectly()
    {
        // Arrange
        var manager = new DynamicSegmentManager();
        long totalBytes = 100 * 1024 * 1024; // 100 MB
        var jobId = Guid.NewGuid();
        manager.InitializeNewJob(jobId, totalBytes, 2);

        var segments = manager.GetSegments();
        var seg0 = segments[0]; // [0 to 52,428,799] (50 MB)
        var seg1 = segments[1]; // [52,428,800 to 104,857,599] (50 MB)

        // Simulate seg0 being very fast (downloaded 45 MB of its 50 MB)
        int downloadedOnSeg0 = 45 * 1024 * 1024;
        seg0.Advance(downloadedOnSeg0); // Remaining on seg0: 5 MB

        // Seg1 has downloaded 0 bytes (remaining: 50 MB)
        long minThreshold = 5 * 1024 * 1024; // 5 MB

        // Act - Trigger Dynamic Bisection
        bool bisected = manager.TryBisectLargestSegment(minThreshold, out var newSegment);

        // Assert
        Assert.True(bisected);
        Assert.NotNull(newSegment);

        // Seg1 should have been clamped at midpoint
        var allSegments = manager.GetSegments();
        Assert.Equal(3, allSegments.Count);

        // Midpoint of seg1: 52,428,800 + 25,000,000 = 77,428,800
        var (_, seg1NewEnd, _) = seg1.GetProgress();
        Assert.Equal(newSegment.StartByte - 1, seg1NewEnd);
        Assert.Equal(104_857_599, newSegment.EndByte);

        // Verify that seg1 and newSegment together cover the exact original range of seg1
        long combined = (seg1NewEnd - seg1.StartByte + 1) + (newSegment.EndByte - newSegment.StartByte + 1);
        Assert.Equal(50 * 1024 * 1024, combined);
    }

    [Fact]
    public void TryBisectLargestSegment_BelowThreshold_RejectsBisection()
    {
        // Arrange
        var manager = new DynamicSegmentManager();
        long totalBytes = 6 * 1024 * 1024; // 6 MB
        var jobId = Guid.NewGuid();
        manager.InitializeNewJob(jobId, totalBytes, 2); // 3 MB each

        var segments = manager.GetSegments();
        // Advance both segments so remaining on each is under 2 MB
        segments[0].Advance(2 * 1024 * 1024);
        segments[1].Advance(2 * 1024 * 1024);

        long minThreshold = 5 * 1024 * 1024; // 5 MB threshold

        // Act
        bool bisected = manager.TryBisectLargestSegment(minThreshold, out var newSegment);

        // Assert - Should refuse to split small remainder
        Assert.False(bisected);
        Assert.Null(newSegment);
        Assert.Equal(2, manager.GetSegments().Count);
    }

    [Fact]
    public void TryGetUnassignedSegment_RetrievesIncompleteUnassignedSegmentsSequentially()
    {
        // Arrange
        var manager = new DynamicSegmentManager();
        long totalBytes = 20 * 1024 * 1024;
        manager.InitializeNewJob(Guid.NewGuid(), totalBytes, 3);

        // Act & Assert - Initial state: all 3 segments unassigned
        Assert.True(manager.TryGetUnassignedSegment(out var seg1));
        Assert.NotNull(seg1);
        Assert.Equal(0, seg1.SegmentIndex);
        Assert.True(seg1.IsAssigned);

        Assert.True(manager.TryGetUnassignedSegment(out var seg2));
        Assert.NotNull(seg2);
        Assert.Equal(1, seg2.SegmentIndex);
        Assert.True(seg2.IsAssigned);

        Assert.True(manager.TryGetUnassignedSegment(out var seg3));
        Assert.NotNull(seg3);
        Assert.Equal(2, seg3.SegmentIndex);
        Assert.True(seg3.IsAssigned);

        // No more unassigned segments remain
        Assert.False(manager.TryGetUnassignedSegment(out var segNone));
        Assert.Null(segNone);

        // Release seg2 (e.g. paused/interrupted)
        seg2.IsAssigned = false;
        Assert.True(manager.TryGetUnassignedSegment(out var segReclaimed));
        Assert.Same(seg2, segReclaimed);

        // Complete seg1
        seg1.Advance((int)(seg1.EndByte - seg1.StartByte + 1));
        Assert.True(seg1.IsCompleted);
        seg1.IsAssigned = false;

        // Completed segment is not returned as unassigned
        Assert.False(manager.TryGetUnassignedSegment(out _));
    }

    [Fact]
    public void InitializeResumedJob_DeepCopiesSegmentsToPreventCrossSessionLeakage()
    {
        // Arrange
        var manager = new DynamicSegmentManager();
        var originalSegment = new Models.DownloadSegmentState(0, 1000, 500)
        {
            SegmentIndex = 0,
            IsAssigned = true
        };

        // Act
        manager.InitializeResumedJob(Guid.NewGuid(), 1000, [originalSegment]);
        var resumedSegments = manager.GetSegments();

        // Assert
        Assert.Single(resumedSegments);
        var copy = resumedSegments[0];
        Assert.NotSame(originalSegment, copy);
        Assert.Equal(originalSegment.StartByte, copy.StartByte);
        Assert.Equal(originalSegment.EndByte, copy.EndByte);
        Assert.Equal(originalSegment.CurrentByte, copy.CurrentByte);
        Assert.False(copy.IsAssigned); // Reset for new session
    }
}
