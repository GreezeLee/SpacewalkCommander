using System.IO;
using UrevoSpacewalkCommander.Configuration;

namespace UrevoSpacewalkCommander.Tests;

/// <summary>
/// Tests the store that remembers the last speed and incline.
/// </summary>
public sealed class TreadmillTargetsStoreTests : IDisposable
{
  /// <summary>Temporary settings folder of one test; not created up front, as on a first start.</summary>
  private readonly string _directory = Path.Combine(Path.GetTempPath(), "UrevoSpacewalkCommanderTests", Guid.NewGuid().ToString("N"));

  /// <summary>Saved targets read back unchanged, also when the folder did not exist yet.</summary>
  [Fact]
  public void Save_RoundTrips()
  {
    var store = new TreadmillTargetsStore(_directory);

    store.Save(new TreadmillTargets(3.7, 4.5));
    store.Save(new TreadmillTargets(4.2, 6));

    Assert.Equal(new TreadmillTargets(4.2, 6), store.Load());
    Assert.False(File.Exists(Path.Combine(_directory, "last-targets.json.tmp")));
  }

  /// <summary>Without a saved file there are no targets to restore.</summary>
  [Fact]
  public void Load_MissingFile_ReturnsNull()
  {
    Assert.Null(new TreadmillTargetsStore(_directory).Load());
  }

  /// <summary>A file with a missing value or broken JSON is reported as invalid data.</summary>
  /// <param name="content">The file content.</param>
  [Theory]
  [InlineData("{ \"speedKmh\": 3.5 }")]
  [InlineData("{ not json")]
  public void Load_InvalidFile_ThrowsInvalidData(string content)
  {
    Directory.CreateDirectory(_directory);
    File.WriteAllText(Path.Combine(_directory, "last-targets.json"), content);

    Assert.Throws<InvalidDataException>(() => new TreadmillTargetsStore(_directory).Load());
  }

  /// <summary>Removes the temporary folder.</summary>
  public void Dispose()
  {
    if (Directory.Exists(_directory))
    {
      Directory.Delete(_directory, recursive: true);
    }
  }
}
