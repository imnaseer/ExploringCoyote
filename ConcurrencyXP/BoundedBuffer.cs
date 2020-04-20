using Microsoft.Coyote.Random;
using Microsoft.Coyote.Specifications;
using Microsoft.Coyote.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ConcurrencyXP
{
    public class BoundedBuffer
    {
        private Generator randomGenerator;
        private Dictionary<int, TaskCompletionSource<bool>> waitMap;
        private CustomAsyncLock monitorLock;

        private object[] buffer;
        private int putAt;
        private int takeAt;
        private int occupied;

        public BoundedBuffer(int bufferLength)
        {
            monitorLock = new CustomAsyncLock();
            waitMap = new Dictionary<int, TaskCompletionSource<bool>>();
            randomGenerator = Generator.Create();

            buffer = new object[bufferLength];
            putAt = 0;
            takeAt = 0;
            occupied = 0;
        }

        public async Task Put(int taskIndex, object x)
        {
            await monitorLock.AcquireLock();

            Console.WriteLine($"Putting from task {taskIndex} - " + SystemSummary());

            while (occupied == buffer.Length)
            {
                var waitTask = Wait(taskIndex);
                monitorLock.ReleaseLock();
                await waitTask;
                await monitorLock.AcquireLock();
            }

            Notify();
            ++occupied;
            putAt %= buffer.Length;
            buffer[putAt++] = x;

            Console.WriteLine($"Completed Putting from task {taskIndex} - " + SystemSummary());

            monitorLock.ReleaseLock();
        }

        public async Task<object> Take(int taskIndex)
        {
            await monitorLock.AcquireLock();

            Console.WriteLine($"Taking from task {taskIndex} - " + SystemSummary());

            while (occupied == 0)
            {
                var waitTask = Wait(taskIndex);
                monitorLock.ReleaseLock();
                await waitTask;
                await monitorLock.AcquireLock();
            }

            Notify();
            --occupied;
            takeAt %= buffer.Length;
            var returnVal = buffer[takeAt++];

            Console.WriteLine($"Done Taking from task {taskIndex} - " + SystemSummary());

            monitorLock.ReleaseLock();
            return returnVal;
        }

        public Task Wait(int taskIndex)
        {
            waitMap[taskIndex] = TaskCompletionSource.Create<bool>();
            return waitMap[taskIndex].Task;
        }

        public void Notify()
        {
            if (waitMap.Count == 0)
            {
                return;
            }

            var index = randomGenerator.NextInteger(waitMap.Count);
            var taskId = waitMap.Keys.ElementAt(index);

            SignalTask(taskId);
        }

        public void NotifyAll()
        {
            foreach (var key in waitMap.Keys.ToList())
            {
                SignalTask(key);
            }
        }

        private void SignalTask(int taskId)
        {
            TaskCompletionSource<bool> tcs;
            waitMap.Remove(taskId, out tcs);

            tcs.SetResult(true);
        }

        private string SystemSummary()
        {
            var waitingTasks = string.Join(", ", waitMap.Keys.ToList());
            return $"Buffer Size: {occupied}; Blocked Tasks: {{ {waitingTasks} }}";
        }
    }
}
