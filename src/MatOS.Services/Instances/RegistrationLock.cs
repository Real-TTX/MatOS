namespace MatOS.Services.Instances;

/// <summary>
/// Makes counting the recent registrations and adding a new one a single step. Counted first and inserted later, sixty requests that
/// arrive together all see the count before any of them was added, and all get through a limit of five. One service writes one SQLite
/// file, so a lock in the process is enough.
/// </summary>
public sealed class RegistrationLock
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Release(_semaphore);
    }

    private sealed class Release(SemaphoreSlim semaphore) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
