using Microsoft.Coyote.Tasks;
using System;
using System.Collections.Generic;

namespace ConcurrencyXP
{
    public class Program
    {
        public static BoundedBuffer boundedBuffer;

        public static Task Reader(int taskIndex)
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    object x = await boundedBuffer.Take(taskIndex);
                }
            });
        }

        public static Task Writer(int taskIndex)
        {
            return Task.Run(async () =>
            {
                while (true)
                {
                    await boundedBuffer.Put(taskIndex, new object());
                }
            });
        }

        [Microsoft.Coyote.SystematicTesting.Test]
        public static async Task ReaderWriterTest()
        {
            Console.WriteLine("Starting ReaderWriterTest");

            boundedBuffer = new BoundedBuffer(bufferLength: 1);

            var tasks = new List<Task>();
            tasks.Add(Reader(0));
            tasks.Add(Reader(1));
            tasks.Add(Writer(2));

            await Task.WhenAll(tasks.ToArray());
        }
        
        public static void Main(string[] args)
        {
            var task = ReaderWriterTest();
            Task.WaitAll(task);
        }
    }
}
