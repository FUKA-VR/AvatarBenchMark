#pragma once
#include <algorithm>
#include <cstdint>
#include <utility>
#include <vector>

// GPU timestamp intervals can overlap when a camera renders another camera or a shadow map.
// Count the union so nested work contributes once and gaps between independent renders do not.
inline uint64_t IntervalUnionTicks(std::vector<std::pair<uint64_t, uint64_t>> intervals) {
    if (intervals.empty()) return 0;
    std::sort(intervals.begin(), intervals.end());
    uint64_t first = intervals.front().first, last = intervals.front().second, total = 0;
    for (size_t i = 1; i < intervals.size(); ++i) {
        if (intervals[i].first <= last) last = (std::max)(last, intervals[i].second);
        else { total += last - first; first = intervals[i].first; last = intervals[i].second; }
    }
    return total + last - first;
}
