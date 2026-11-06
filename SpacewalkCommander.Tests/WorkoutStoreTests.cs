using System.IO;
using SpacewalkCommander.Workouts;

namespace SpacewalkCommander.Tests;

/// <summary>
/// Tests the local workout archive.
/// </summary>
public sealed class WorkoutStoreTests : IDisposable
{
  /// <summary>Temporary archive folder of one test.</summary>
  private readonly string _directory = Path.Combine(Path.GetTempPath(), "SpacewalkCommanderTests", Guid.NewGuid().ToString("N"));

  /// <summary>A saved workout reads back unchanged and moves to "uploaded" after the upload.</summary>
  [Fact]
  public void SavePending_RoundTripsAndMovesOnUpload()
  {
    var store = new WorkoutStore(_directory);
    DateTimeOffset start = new(2026, 11, 6, 18, 0, 0, TimeSpan.FromHours(1));
    var session = new WorkoutSession(
      Guid.NewGuid(),
      "URTM051",
      start,
      start.AddMinutes(5),
      [new WorkoutEvent(start, WorkoutEventKind.Start), new WorkoutEvent(start.AddMinutes(5), WorkoutEventKind.Stop)],
      [new WorkoutSample(start.AddSeconds(1), 3.5, 2, 1, null, 95)]);

    string path = store.SavePending(session);
    WorkoutSession loaded = WorkoutStore.Load(path);
    store.MarkUploaded(path);

    Assert.Equal(session.Id, loaded.Id);
    Assert.Equal(session.StartTime, loaded.StartTime);
    Assert.Equal(session.Events, loaded.Events);
    Assert.Equal(session.Samples, loaded.Samples);
    Assert.Empty(store.GetPendingFiles());
    Assert.True(File.Exists(Path.Combine(_directory, "uploaded", Path.GetFileName(path))));
  }

  /// <summary>Removes the temporary archive.</summary>
  public void Dispose()
  {
    if (Directory.Exists(_directory))
    {
      Directory.Delete(_directory, recursive: true);
    }
  }
}
