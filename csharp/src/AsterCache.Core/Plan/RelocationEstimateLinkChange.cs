namespace AsterCache.Core.Plan;

public static partial class RelocationEstimate
{
    /// <summary>
    /// 한 플레이의 순서에서, 선정이 구역으로 옮긴 블록(placement)이 있을 때 이음이 어떻게 바뀌는지 셉니다.
    /// 이음은 같은 장치의 줄에서 이웃한 두 블록 사이(블록 안 조각 사이 포함)의 0 이 아닌 거리입니다.
    /// 위치를 모르는 블록과 볼륨 변경에서는 줄이 끊깁니다. 지금 위치를 모르지만 구역에 놓인 블록은 가상 배치에서만 위치가 있습니다.
    /// </summary>
    private static RelocationEstimateLinkChange LinkChange(
        IReadOnlyList<RelocationEstimateBlock> ordered,
        IReadOnlyDictionary<BlockKey, RelocationEstimatePhysicalExtent> placement,
        RelocationEstimateModel model,
        long diskBytes)
    {
        long removedLinks = 0, crossingLinks = 0, insideLinks = 0;
        double removedBefore = 0, crossingMs = 0, crossingBefore = 0, insideMs = 0, insideBefore = 0;

        void Pair(long before, long after, int selectedEnds)
        {
            double beforeMs = model.PositionMs(before, diskBytes);
            double afterMs = model.PositionMs(after, diskBytes);
            if (before != 0 && after == 0)
            {
                removedLinks++;
                removedBefore += beforeMs;
            }
            else if (after != 0 && selectedEnds == 1)
            {
                crossingLinks++;
                crossingMs += afterMs;
                crossingBefore += beforeMs;
            }
            else if (after != 0 && selectedEnds == 2)
            {
                insideLinks++;
                insideMs += afterMs;
                insideBefore += beforeMs;
            }
        }

        foreach (IGrouping<string, RelocationEstimateBlock> device in ordered.Where(block => block.DeviceKey is not null)
            .GroupBy(block => block.DeviceKey!, StringComparer.OrdinalIgnoreCase))
        {
            long? previousCurrentEnd = null, previousVirtualEnd = null;
            bool previousSelected = false;
            string? previousVolume = null;
            foreach (RelocationEstimateBlock block in device.OrderBy(block => block.FirstObservedUs)
                .ThenBy(block => block.Path, StringComparer.OrdinalIgnoreCase).ThenBy(block => block.BlockNumber))
            {
                bool selected = placement.TryGetValue(new BlockKey(block.Path, block.BlockNumber), out RelocationEstimatePhysicalExtent? extent);
                IReadOnlyList<RelocationEstimatePhysicalExtent> current = block.CurrentExtents;
                bool currentLocated = current.Count > 0;
                bool virtualLocated = selected || currentLocated;
                if (previousVolume is not null && !string.Equals(previousVolume, block.VolumeKey, StringComparison.OrdinalIgnoreCase))
                {
                    previousCurrentEnd = null;
                    previousVirtualEnd = null;
                }

                if (!currentLocated && !virtualLocated)
                {
                    previousCurrentEnd = null;
                    previousVirtualEnd = null;
                    previousVolume = null;
                    continue;
                }

                IReadOnlyList<RelocationEstimatePhysicalExtent> virtualExtents = selected ? [extent!] : current;
                for (int i = 1; i < current.Count; i++)
                {
                    long gap = Math.Abs(checked(current[i].StartByte - current[i - 1].EndByte));
                    Pair(gap, selected ? 0 : gap, selected ? 2 : 0);
                }

                if (previousCurrentEnd is not null || previousVirtualEnd is not null)
                {
                    long before = previousCurrentEnd is { } pc && currentLocated ? Math.Abs(checked(current[0].StartByte - pc)) : 0;
                    long after = previousVirtualEnd is { } pv && virtualLocated ? Math.Abs(checked(virtualExtents[0].StartByte - pv)) : 0;
                    Pair(before, after, (previousSelected ? 1 : 0) + (selected ? 1 : 0));
                }

                previousCurrentEnd = currentLocated ? current[^1].EndByte : null;
                previousVirtualEnd = virtualLocated ? virtualExtents[^1].EndByte : null;
                previousSelected = selected;
                previousVolume = block.VolumeKey;
            }
        }

        return new RelocationEstimateLinkChange(
            new RelocationEstimateLinkGroup(removedLinks, 0, removedBefore),
            new RelocationEstimateLinkGroup(crossingLinks, crossingMs, crossingBefore),
            new RelocationEstimateLinkGroup(insideLinks, insideMs, insideBefore));
    }
}
