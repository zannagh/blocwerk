using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Services;

/// <summary>
/// One hold row at the target generation with the boulder mark that won it, and every one of the
/// boulder's own holds that walked back to it. The source list matters: without it a convergence
/// (several of today's holds descending from one older row) is indistinguishable from a hold that
/// found no ancestor at all, and the detail page reports missing holds that are not missing.
/// </summary>
/// <param name="Type">The boulder's mark on the row (start/top/normal).</param>
/// <param name="Usage">The boulder's usage rule for the row.</param>
/// <param name="SourceHoldIds">
/// The boulder's own hold ids that resolved here. Mutable by the walk that builds it — entries are
/// appended as later marks converge on the same row — and read only once the walk has finished.
/// </param>
internal readonly record struct MappedMark(HoldType Type, HoldUsage Usage, List<Guid> SourceHoldIds);
