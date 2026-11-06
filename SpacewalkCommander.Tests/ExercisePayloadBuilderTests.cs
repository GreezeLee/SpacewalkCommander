using System.Text.Json;
using SpacewalkCommander.GoogleHealth;
using SpacewalkCommander.Workouts;

namespace SpacewalkCommander.Tests;

/// <summary>
/// Tests the mapping of a workout to the Google Health API exercise JSON.
/// </summary>
public sealed class ExercisePayloadBuilderTests
{
  /// <summary>Start time of the test workout (UTC+1).</summary>
  private static readonly DateTimeOffset s_start = new(2026, 11, 6, 18, 0, 0, TimeSpan.FromHours(1));

  /// <summary>Times are UTC RFC 3339, offsets and durations are seconds with an "s" suffix.</summary>
  [Fact]
  public void Format_UsesApiConventions()
  {
    Assert.Equal("2026-11-06T17:00:00.000Z", ExercisePayloadBuilder.FormatTime(s_start));
    Assert.Equal("3600s", ExercisePayloadBuilder.FormatOffset(s_start));
    Assert.Equal("1800s", ExercisePayloadBuilder.FormatDuration(TimeSpan.FromMinutes(30)));
  }

  /// <summary>The JSON has the documented shape and omits unknown values.</summary>
  [Fact]
  public void Build_ProducesDocumentedJson()
  {
    WorkoutSession session = CreateSession();
    WorkoutSummary summary = WorkoutAnalyzer.Analyze(session, bodyWeightKg: null);

    string json = GoogleHealthClient.Serialize(ExercisePayloadBuilder.Build(session, summary));
    using JsonDocument document = JsonDocument.Parse(json);
    JsonElement exercise = document.RootElement.GetProperty("exercise");
    JsonElement metrics = exercise.GetProperty("metricsSummary");

    Assert.Equal("ACTIVELY_MEASURED", document.RootElement.GetProperty("dataSource").GetProperty("recordingMethod").GetString());
    Assert.Equal("TREADMILL_WALK", exercise.GetProperty("exerciseType").GetString());
    Assert.Equal("2026-11-06T17:00:00.000Z", exercise.GetProperty("interval").GetProperty("startTime").GetString());
    Assert.Equal("3600s", exercise.GetProperty("interval").GetProperty("endUtcOffset").GetString());
    Assert.Equal("1200s", exercise.GetProperty("activeDuration").GetString());
    Assert.Equal(1_200_000, metrics.GetProperty("distanceMillimeters").GetDouble());
    Assert.Equal(1000, metrics.GetProperty("averageSpeedMillimetersPerSecond").GetDouble());
    Assert.Equal(60_000, metrics.GetProperty("elevationGainMillimeters").GetDouble());
    Assert.False(metrics.TryGetProperty("averageHeartRateBeatsPerMinute", out _));
    Assert.False(metrics.TryGetProperty("caloriesKcal", out _));
    Assert.Equal(["START", "STOP"], exercise.GetProperty("exerciseEvents").EnumerateArray()
      .Select(item => item.GetProperty("exerciseEventType").GetString()));
    Assert.Equal(2, exercise.GetProperty("splitSummaries").GetArrayLength());
  }

  /// <summary>
  /// Creates a 20-minute workout at 3.6 km/h and 5 % incline with a distance counter (1200 m).
  /// </summary>
  /// <returns>The workout.</returns>
  private static WorkoutSession CreateSession()
  {
    DateTimeOffset end = s_start.AddMinutes(20);
    WorkoutSample[] samples =
    [
      .. Enumerable.Range(0, 1201).Select(
        second => new WorkoutSample(s_start.AddSeconds(second), 3.6, 5, second, null, null)),
    ];
    return new WorkoutSession(
      Guid.NewGuid(),
      "URTM051",
      s_start,
      end,
      [new WorkoutEvent(s_start, WorkoutEventKind.Start), new WorkoutEvent(end, WorkoutEventKind.Stop)],
      samples);
  }
}
