namespace AsterCache.Core.Plan;

/// <summary>위치 잡는 시간 모형의 값입니다. 이음 거리 d 의 시간은 d 가 0 이면 0, 아니면 기본 시간 + 추가 시간 × sqrt(min(d, 디스크 크기) / 디스크 크기) 입니다.</summary>
public sealed record RelocationEstimateModel(double PositionBaseMs, double PositionFullMs, double TransferMBps, long? DiskBytes = null)
{
    public const double DefaultPositionBaseMs = 6.0;
    public const double DefaultPositionFullMs = 12.0;
    public const double DefaultTransferMBps = 150.0;

    public static RelocationEstimateModel Default { get; } = new(DefaultPositionBaseMs, DefaultPositionFullMs, DefaultTransferMBps);

    public double PositionMs(long distanceBytes, long diskBytes)
    {
        if (distanceBytes <= 0)
        {
            return 0;
        }

        double fraction = diskBytes <= 0 ? 1.0 : Math.Min(distanceBytes, diskBytes) / (double)diskBytes;
        return PositionBaseMs + PositionFullMs * Math.Sqrt(fraction);
    }

    /// <summary>전송률의 MB 는 10^6 바이트입니다(장치 보정 파일의 전송률과 같은 단위).</summary>
    public double TransferMs(long bytes) => bytes / (TransferMBps * 1000d);
}

public sealed record RelocationEstimateDistribution(long Count, double? Median, long? P90, long? Max);

public sealed record RelocationEstimateSegmentStats(
    long Segments,
    RelocationEstimateDistribution SizeBytes,
    RelocationEstimateDistribution LinkDistanceBytes);

/// <summary>이음 묶음 하나의 수와 시간 합입니다. Ms 는 가상 배치에서의 시간, BeforeMs 는 같은 이음의 지금 배치에서의 시간입니다.</summary>
public sealed record RelocationEstimateLinkGroup(long Links, double Ms, double BeforeMs);

/// <summary>
/// 선정으로 바뀌는 이음의 내역입니다. 선정 전후의 모형 위치 잡는 시간 차이는
/// (Crossing.Ms - Crossing.BeforeMs) + (Inside.Ms - Inside.BeforeMs) - Removed.BeforeMs 와 같습니다.
/// </summary>
/// <param name="Removed">지금은 0 이 아닌 거리였다가 0 이 되어 없어진 이음.</param>
/// <param name="Crossing">한쪽만 구역에 놓여 구역과 구역 밖을 오가는, 가상 배치에서 0 이 아닌 이음.</param>
/// <param name="Inside">양쪽 모두 구역에 놓였는데 0 이 아닌 이음.</param>
public sealed record RelocationEstimateLinkChange(
    RelocationEstimateLinkGroup Removed,
    RelocationEstimateLinkGroup Crossing,
    RelocationEstimateLinkGroup Inside);

/// <summary>한 배치에서 모형으로 센 지표입니다. 이음은 앞 구간과 바로 이어 읽히는 두 구간 사이의 0 이 아닌 거리입니다.</summary>
public sealed record RelocationEstimateModelMetrics(
    long Segments,
    long Links,
    double PositionMs,
    double TransferMs,
    double? MedianLinkDistanceBytes,
    long? P90LinkDistanceBytes);

/// <summary>읽힌 순서에서 이웃하고 디스크에서도 바로 이어지는 블록 조각의 묶음입니다.</summary>
internal sealed class ReadSegment(string volumeKey, long startByte)
{
    public string VolumeKey { get; } = volumeKey;

    public long StartByte { get; } = startByte;

    public long EndByte { get; set; } = startByte;

    public long Bytes => EndByte - StartByte;

    /// <summary>같은 장치의 앞 구간과 사이에 위치를 모르는 블록이 끼지 않고 바로 이어 읽힙니다.</summary>
    public bool LinkedFromPrevious { get; init; }

    /// <summary>앞 구간과의 이음이 한 블록 안의 조각 끊김입니다(그 블록은 둘로 나눠 옮길 수 없습니다).</summary>
    public bool BreakInsideBlock { get; init; }

    public double WeightSum { get; set; }

    public int BlockCount { get; set; }

    public bool Selectable { get; set; } = true;

    public List<RelocationEstimateBlock> Blocks { get; } = [];
}

/// <summary>읽기 구간 묶기와 분포 계산입니다. 파일 시스템에 접근하지 않습니다.</summary>
internal static class RelocationEstimateSegments
{
    /// <summary>장치별로 처음 읽힌 순서의 블록을 구간으로 묶습니다. 위치를 모르는 블록과 볼륨이 바뀌는 곳에서는 이음이 끊깁니다.</summary>
    public static List<ReadSegment> Build(IReadOnlyList<RelocationEstimateBlock> ordered, Func<RelocationEstimateBlock, bool> selectable)
    {
        var result = new List<ReadSegment>();
        foreach (IGrouping<string, RelocationEstimateBlock> device in ordered.Where(block => block.DeviceKey is not null)
            .GroupBy(block => block.DeviceKey!, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            ReadSegment? current = null;
            bool chained = false;
            foreach (RelocationEstimateBlock block in device.OrderBy(block => block.FirstObservedUs)
                .ThenBy(block => block.Path, StringComparer.OrdinalIgnoreCase).ThenBy(block => block.BlockNumber))
            {
                if (block.CurrentExtents.Count == 0
                    || (current is not null && !string.Equals(current.VolumeKey, block.VolumeKey, StringComparison.OrdinalIgnoreCase)))
                {
                    current = null;
                    chained = false;
                    if (block.CurrentExtents.Count == 0)
                    {
                        continue;
                    }
                }

                bool ok = selectable(block);
                bool firstPiece = true;
                foreach (RelocationEstimatePhysicalExtent extent in block.CurrentExtents)
                {
                    if (current is not null && extent.StartByte == current.EndByte)
                    {
                        current.EndByte = extent.EndByte;
                    }
                    else
                    {
                        current = new ReadSegment(block.VolumeKey, extent.StartByte)
                        {
                            LinkedFromPrevious = chained,
                            BreakInsideBlock = chained && !firstPiece,
                        };
                        current.EndByte = extent.EndByte;
                        result.Add(current);
                        chained = true;
                    }

                    if (current.Blocks.Count == 0 || !ReferenceEquals(current.Blocks[^1], block))
                    {
                        current.Blocks.Add(block);
                        // 구간마다 물리적으로 든 모든 블록을 셉니다. 블록 안 끊김으로 두 구간에 걸친 블록의 이중 계산은 이음 가중치 평균에서 뺍니다.
                        current.BlockCount++;
                        current.WeightSum += block.Weight;
                        current.Selectable &= ok;
                    }

                    firstPiece = false;
                }
            }
        }

        return result;
    }

    public static RelocationEstimateModelMetrics Metrics(IReadOnlyList<ReadSegment> segments, RelocationEstimateModel model, long diskBytes)
    {
        var distances = new List<long>();
        double positionMs = 0;
        long bytes = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            bytes = checked(bytes + segments[i].Bytes);
            if (segments[i].LinkedFromPrevious)
            {
                long distance = LinkDistance(segments[i - 1], segments[i]);
                distances.Add(distance);
                positionMs += model.PositionMs(distance, diskBytes);
            }
        }

        RelocationEstimateDistribution distribution = Distribution(distances);
        return new RelocationEstimateModelMetrics(segments.Count, distances.Count, positionMs, model.TransferMs(bytes),
            distribution.Median, distribution.P90);
    }

    public static RelocationEstimateSegmentStats Stats(IReadOnlyList<ReadSegment> segments)
    {
        var distances = new List<long>();
        for (int i = 1; i < segments.Count; i++)
        {
            if (segments[i].LinkedFromPrevious)
            {
                distances.Add(LinkDistance(segments[i - 1], segments[i]));
            }
        }

        return new RelocationEstimateSegmentStats(segments.Count, Distribution([.. segments.Select(segment => segment.Bytes)]),
            Distribution(distances));
    }

    public static long LinkDistance(ReadSegment previous, ReadSegment next) => Math.Abs(checked(next.StartByte - previous.EndByte));

    public static long MaximumLinkDistance(IReadOnlyList<ReadSegment> segments)
    {
        long maximum = 0;
        for (int i = 1; i < segments.Count; i++)
        {
            if (segments[i].LinkedFromPrevious)
            {
                maximum = Math.Max(maximum, LinkDistance(segments[i - 1], segments[i]));
            }
        }

        return maximum;
    }

    private static RelocationEstimateDistribution Distribution(List<long> values)
    {
        if (values.Count == 0)
        {
            return new RelocationEstimateDistribution(0, null, null, null);
        }

        values.Sort();
        double median = values.Count % 2 == 1
            ? values[values.Count / 2]
            : values[values.Count / 2 - 1] / 2d + values[values.Count / 2] / 2d;
        int p90Index = Math.Max(0, (int)Math.Ceiling(values.Count * 0.9) - 1);
        return new RelocationEstimateDistribution(values.Count, median, values[p90Index], values[^1]);
    }
}
