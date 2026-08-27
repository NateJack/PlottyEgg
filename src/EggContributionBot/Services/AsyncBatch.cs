namespace EggContribBot;

public static class AsyncBatch {
    public static async Task<IReadOnlyList<TResult>> SelectWithConcurrencyAsync<TSource, TResult>(
        IEnumerable<TSource> source,
        int maxConcurrency,
        Func<TSource, Task<TResult>> selector,
        CancellationToken cancellationToken = default) {
        var items = source.ToList();
        if(items.Count == 0) {
            return [];
        }

        using var gate = new SemaphoreSlim(Math.Max(1, maxConcurrency), Math.Max(1, maxConcurrency));
        var tasks = items.Select(async (item, index) => {
            await gate.WaitAsync(cancellationToken);
            try {
                return (Index: index, Result: await selector(item));
            } finally {
                gate.Release();
            }
        });

        var results = await Task.WhenAll(tasks);
        return results
            .OrderBy(result => result.Index)
            .Select(result => result.Result)
            .ToList();
    }
}
