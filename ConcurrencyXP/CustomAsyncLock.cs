using Microsoft.Coyote.Tasks;

namespace ConcurrencyXP
{
    public class CustomAsyncLock
    {
        private AsyncLock asyncLock;
        private AsyncLock.Releaser releaser;

        public CustomAsyncLock()
        {
            this.asyncLock = AsyncLock.Create();
        }
        public async Task AcquireLock()
        {
            this.releaser = await this.asyncLock.AcquireAsync();
        }

        public void ReleaseLock()
        {
            this.releaser.Dispose();
        }
    }
}
