namespace NetworkA.FileProcessing;

// FFmpeg is already multi-threaded and saturates all CPU cores on its own.
// Running multiple FFmpeg processes in parallel (one per file via Parallel.ForEachAsync)
// creates contention without improving throughput. This semaphore pins the total number
// of concurrent FFmpeg invocations to 1, shared across both conversion and splitting.
internal static class FfmpegGate
{
    internal static readonly SemaphoreSlim Semaphore = new(1, 1);
}
