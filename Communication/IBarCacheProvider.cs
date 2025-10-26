using System;
using IndicatorService;

namespace RemoteIndicator.ATAS.Communication
{
    /// <summary>
    /// Interface for querying bar cache
    ///
    /// Design Purpose (v4.1):
    /// - Decouples DataPuller (background thread) from direct cache access
    /// - Avoids background thread accessing ATAS API (violates thread safety)
    /// - DataTerminal implements this interface, providing cache query method
    /// - DataPuller uses this interface, thread-safe query without ATAS API calls
    ///
    /// Thread Safety:
    /// - Implementation MUST lock cache during query
    /// - Background thread (DataPuller.RepLoop) calls QueryBars()
    /// - Main thread (DataTerminal.OnCalculate) updates cache
    ///
    /// v4.1 Design: Pass full DataRequest (not just time range)
    /// - Enables timeframe validation (request.Resolution + request.NumUnits)
    /// - DataTerminal verifies request matches current chart before returning bars
    /// - Prevents data mismatch between Service request and ATAS chart
    /// </summary>
    public interface IBarCacheProvider
    {
        /// <summary>
        /// Query bars from cache based on DataRequest
        /// </summary>
        /// <param name="request">DataRequest from Service (includes time range, symbol, resolution, numUnits)</param>
        /// <returns>DataResponse with bars in range (empty if timeframe mismatch)</returns>
        /// <remarks>
        /// MUST be thread-safe: lock cache during query
        /// Called from background thread (DataPuller.RepLoop)
        /// MUST validate: request.Resolution/NumUnits matches current chart
        /// </remarks>
        DataResponse QueryBars(DataRequest request);
    }
}
